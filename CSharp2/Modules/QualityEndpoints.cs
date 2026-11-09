using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class QualityEndpoints
{
    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var quality = endpoints.MapGroup("/api/quality-inspections").RequireAuthorization();
        quality.MapGet("/", ListInspections);
        quality.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Quality.Read", conceal: true);
            var inspection = await InspectionViews(db.QualityInspections
                    .Where(x => x.Id == id)).SingleOrDefaultAsync()
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(inspection);
        });
        quality.MapPost("/{id:guid}/decision", (Guid id, DecisionRequest request, StockFlowDbContext db,
            AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "QualityInspection.Decide", new { id, request }, async () =>
            {
                await access.RequireAsync("Quality.Inspect", conceal: true);
                if (!await db.QualityInspections.AnyAsync(x => x.Id == id))
                    throw new ApiException(404, "Resource not found.");
            }, async () =>
            {
                var workId = await db.QualityInspections.Where(x => x.Id == id)
                    .Select(x => x.StockLot.ProductionOutput != null ? (Guid?)x.StockLot.ProductionOutput.ProductionOrderId : null)
                    .SingleAsync();
                ProductionOrder? work = null;
                if (workId.HasValue)
                {
                    var salesId = await db.ProductionOrders.Where(x => x.Id == workId.Value)
                        .Select(x => x.SalesOrderItem.SalesOrderId).SingleAsync();
                    await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM [SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {salesId}")
                        .SingleAsync();
                    work = await db.ProductionOrders.FromSqlInterpolated($"SELECT * FROM [ProductionOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {workId.Value}")
                        .SingleAsync();
                    if (work.Status != ProductionOrderStatus.AwaitingQC)
                        throw new ApiException(409, "The production order is not awaiting its final quality decision.");
                }
                var inspection = await db.QualityInspections.Include(x => x.StockLot).ThenInclude(x => x.Balance)
                    .SingleAsync(x => x.Id == id);
                if (request.Version is null || request.Version.Length != 8 || !inspection.Version.SequenceEqual(request.Version))
                    throw new ApiException(409, "The inspection changed. Reload it and retry.");
                if (inspection.Status != QualityInspectionStatus.Pending || inspection.StockLot.QualityStatus != QualityStatus.Pending)
                    throw new ApiException(409, "This lot already has a final quality decision.");
                if (request.Decision is not (QualityInspectionStatus.Pass or QualityInspectionStatus.Fail))
                    throw new ApiException(400, "A final Pass or Fail decision is required.");
                var notes = request.Notes?.Trim() ?? "";
                if (notes.Length > 2000) throw new ApiException(400, "Inspection notes cannot exceed 2000 characters.");
                var postedBy = inspection.StockLot.GoodsReceiptItemId.HasValue
                    ? await db.GoodsReceiptItems.Where(x => x.Id == inspection.StockLot.GoodsReceiptItemId)
                        .Select(x => x.GoodsReceipt.PostedByUserId).SingleAsync()
                    : await db.ProductionOutputs.Where(x => x.Id == inspection.StockLot.ProductionOutputId)
                        .Select(x => x.PostedByUserId).SingleAsync();
                if (postedBy == access.UserId)
                    throw new ApiException(409, "The user who posted the lot's source cannot inspect it.");
                var timestamp = DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(), DateTimeKind.Utc);
                inspection.Status = request.Decision;
                inspection.InspectedByUserId = access.UserId;
                inspection.InspectedAtUtc = timestamp;
                inspection.Notes = notes;
                inspection.StockLot.QualityStatus = request.Decision == QualityInspectionStatus.Pass
                    ? QualityStatus.Released : QualityStatus.Rejected;
                if (work is not null)
                {
                    work.Status = ProductionOrderStatus.Completed;
                    var actor = await access.CurrentAsync();
                    db.AuditEvents.Add(new AuditEvent
                    {
                        ActorUserId = access.UserId, ActorDepartmentId = actor.Employee.DepartmentId,
                        ResourceDepartmentId = work.DepartmentId, EntityType = nameof(ProductionOrder), EntityId = work.Id,
                        Action = "QualityCompleted", RequestId = request.RequestId, CreatedAtUtc = timestamp,
                        DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            inspectionId = id, stockLotId = inspection.StockLotId,
                            qualityStatus = inspection.StockLot.QualityStatus.ToString(), status = work.Status.ToString()
                        })
                    });
                }
                await db.SaveChangesAsync();
                var view = await InspectionViews(db.QualityInspections.Where(x => x.Id == id)).SingleAsync();
                return new CommandResult<InspectionView>(view, nameof(QualityInspection), id, inspection.DepartmentId,
                    "Decided", new { inspection.StockLotId, inspection.Status, inspection.Notes,
                        inspection.InspectedAtUtc, inspection.StockLot.GoodsReceiptItemId,
                        inspection.StockLot.ProductionOutputId, ProductionOrderId = work?.Id,
                        inspection.StockLot.QualityStatus, inspection.StockLot.Balance.OnHand, inspection.StockLot.Balance.Reserved });
            }));
        return endpoints;
    }

    private static async Task<IResult> ListInspections(StockFlowDbContext db, AccessService access,
        string? search = null, Guid? departmentId = null, Guid? productId = null, Guid? goodsReceiptId = null,
        Guid? productionOrderId = null,
        QualityInspectionStatus? status = null, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Quality.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.QualityInspections.AsNoTracking();
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        if (productId.HasValue) query = query.Where(x => x.StockLot.ProductId == productId.Value);
        if (goodsReceiptId.HasValue)
            query = query.Where(x => x.StockLot.GoodsReceiptItem!.GoodsReceiptId == goodsReceiptId.Value);
        if (productionOrderId.HasValue)
            query = query.Where(x => x.StockLot.ProductionOutput!.ProductionOrderId == productionOrderId.Value);
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid inspection status.");
            query = query.Where(x => x.Status == status.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.StockLot.LotNumber.Contains(search) || x.StockLot.Product.Sku.Contains(search)
                || x.StockLot.Product.Name.Contains(search) || x.StockLot.GoodsReceiptItem!.GoodsReceipt.Number.Contains(search)
                || x.StockLot.GoodsReceiptItem!.GoodsReceipt.PurchaseOrder.Number.Contains(search)
                || x.StockLot.ProductionOutput!.ProductionOrder.Number.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await InspectionViews(query.OrderByDescending(x => x.StockLot.ReceivedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static IQueryable<InspectionView> InspectionViews(IQueryable<QualityInspection> query) =>
        query.AsNoTracking().Select(x => new InspectionView(x.Id, x.DepartmentId, x.Status, x.Version,
            x.StockLotId, x.StockLot.LotNumber, x.StockLot.ProductId, x.StockLot.Product.Sku,
            x.StockLot.Product.Name, x.StockLot.Product.BaseUnit, x.StockLot.QualityStatus, DateTime.SpecifyKind(x.StockLot.ReceivedAtUtc, DateTimeKind.Utc),
            x.StockLot.Balance.OnHand, x.StockLot.Balance.Reserved,
            x.StockLot.QualityStatus == QualityStatus.Released ? x.StockLot.Balance.OnHand - x.StockLot.Balance.Reserved : 0,
            x.StockLot.GoodsReceiptItemId, x.StockLot.GoodsReceiptItem != null ? x.StockLot.GoodsReceiptItem.ReceivedQuantity : null,
            x.StockLot.GoodsReceiptItem != null ? x.StockLot.GoodsReceiptItem.GoodsReceiptId : null,
            x.StockLot.GoodsReceiptItem != null ? x.StockLot.GoodsReceiptItem.GoodsReceipt.Number : null,
            x.StockLot.GoodsReceiptItem != null ? x.StockLot.GoodsReceiptItem.GoodsReceipt.PostedByUserId : x.StockLot.ProductionOutput!.PostedByUserId,
            x.StockLot.GoodsReceiptItem != null ? x.StockLot.GoodsReceiptItem.GoodsReceipt.PurchaseOrderId : null,
            x.StockLot.GoodsReceiptItem != null ? x.StockLot.GoodsReceiptItem.GoodsReceipt.PurchaseOrder.Number : null,
            x.InspectedByUserId, x.InspectedAtUtc.HasValue ? DateTime.SpecifyKind(x.InspectedAtUtc.Value, DateTimeKind.Utc) : null, x.Notes,
            x.StockLot.ProductionOutputId,
            x.StockLot.ProductionOutput != null ? x.StockLot.ProductionOutput.ProductionOrderId : null,
            x.StockLot.ProductionOutput != null ? x.StockLot.ProductionOutput.ProductionOrder.Number : null,
            x.StockLot.ProductionOutput != null ? x.StockLot.ProductionOutput.ProducedQuantity : null,
            x.StockLot.ProductionOutput != null ? x.StockLot.ProductionOutput.ScrapQuantity : null,
            x.StockLot.GoodsReceiptItemId != null ? "GoodsReceipt" : "ProductionOutput"));

    public sealed record DecisionRequest(Guid RequestId, byte[]? Version, QualityInspectionStatus Decision, string? Notes);
    public sealed record InspectionView(Guid Id, Guid DepartmentId, QualityInspectionStatus Status, byte[] Version,
        Guid StockLotId, string LotNumber, Guid ProductId, string Sku, string ProductName, string BaseUnit,
        QualityStatus QualityStatus, DateTime ReceivedAtUtc, decimal OnHand, decimal Reserved, decimal Available,
        Guid? GoodsReceiptItemId, decimal? ReceivedQuantity, Guid? GoodsReceiptId, string? GoodsReceiptNumber,
        Guid PostedByUserId, Guid? PurchaseOrderId, string? PurchaseOrderNumber, Guid? InspectedByUserId,
        DateTime? InspectedAtUtc, string Notes, Guid? ProductionOutputId, Guid? ProductionOrderId,
        string? ProductionOrderNumber, decimal? ProducedQuantity, decimal? ScrapQuantity, string SourceType);
}
