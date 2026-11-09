using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class ReceivingEndpoints
{
    public static IEndpointRouteBuilder MapReceivingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var receipts = endpoints.MapGroup("/api/goods-receipts").RequireAuthorization();
        receipts.MapGet("/", ListReceipts);
        receipts.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync("Receipts.Read");
            if (!await db.GoodsReceipts.AnyAsync(x => x.Id == id
                    && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
                throw new ApiException(404, "Resource not found.");
            return Results.Ok(await ReceiptViewAsync(db, id));
        });
        receipts.MapPost("/", (ReceiptRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "GoodsReceipt.Post", request, async () =>
            {
                await access.RequireAsync("Inventory.Receive");
                var departmentId = await db.PurchaseOrders.Where(x => x.Id == request.PurchaseOrderId)
                    .Select(x => (Guid?)x.DepartmentId).SingleOrDefaultAsync()
                    ?? throw new ApiException(404, "Resource not found.");
                await access.RequireAsync("Purchasing.Read", departmentId, conceal: true);
            }, async () =>
            {
                var order = await db.PurchaseOrders.FromSqlInterpolated($"SELECT * FROM [PurchaseOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {request.PurchaseOrderId}")
                    .Include(x => x.Items).SingleOrDefaultAsync()
                    ?? throw new ApiException(404, "Resource not found.");
                if (request.PurchaseOrderVersion is null || request.PurchaseOrderVersion.Length != 8
                    || !order.Version.SequenceEqual(request.PurchaseOrderVersion))
                    throw new ApiException(409, "The purchase order changed. Reload it and retry.");
                if (order.Status is not (PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))
                    throw new ApiException(409, "Only an approved purchase order with an open remainder can receive goods.");
                if (request.Items is null || request.Items.Count is < 1 or > 20
                    || request.Items.Any(x => x is null)
                    || request.Items.Select(x => x.PurchaseOrderItemId).Distinct().Count() != request.Items.Count)
                    throw new ApiException(400, "Provide 1 to 20 distinct purchase order lines.");
                var number = Input.Code(request.Number, 60);
                var lotNumbers = request.Items.Select(x => Input.Code(x.LotNumber, 80)).ToArray();
                if (lotNumbers.Distinct().Count() != lotNumbers.Length)
                    throw new ApiException(400, "Receipt lot numbers must be distinct.");
                var departmentIds = new[] { request.DepartmentId, request.QualityDepartmentId }.Distinct().ToArray();
                if (await db.Departments.CountAsync(x => departmentIds.Contains(x.Id) && x.IsActive) != departmentIds.Length)
                    throw new ApiException(400, "Active receiving and quality departments are required.");

                var received = await db.GoodsReceiptItems.Where(x => x.GoodsReceipt.PurchaseOrderId == order.Id)
                    .GroupBy(x => x.PurchaseOrderItemId)
                    .Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.ReceivedQuantity) })
                    .ToDictionaryAsync(x => x.Id, x => x.Quantity);
                var timestamp = await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
                var receipt = new GoodsReceipt
                {
                    Number = number, DepartmentId = request.DepartmentId, PurchaseOrderId = order.Id,
                    PostedByUserId = access.UserId, PostedAtUtc = timestamp
                };
                db.GoodsReceipts.Add(receipt);
                for (var index = 0; index < request.Items.Count; index++)
                {
                    var line = request.Items[index];
                    var orderLine = order.Items.SingleOrDefault(x => x.Id == line.PurchaseOrderItemId)
                        ?? throw new ApiException(400, "Every receipt line must belong to the selected purchase order.");
                    var quantity = Input.Quantity(line.ReceivedQuantity);
                    if (quantity <= 0) throw new ApiException(400, "Received quantity must be positive.");
                    var priorQuantity = received.GetValueOrDefault(orderLine.Id);
                    if (quantity > orderLine.Quantity - orderLine.CancelledQuantity - priorQuantity)
                        throw new ApiException(409, "Received quantity exceeds the purchase order line's open remainder.");
                    var item = new GoodsReceiptItem
                    {
                        GoodsReceipt = receipt, PurchaseOrderItemId = orderLine.Id, ReceivedQuantity = quantity
                    };
                    db.GoodsReceiptItems.Add(item);
                    var lot = new StockLot
                    {
                        LotNumber = lotNumbers[index], ProductId = orderLine.ProductId,
                        GoodsReceiptItemId = item.Id, QualityStatus = QualityStatus.Pending, ReceivedAtUtc = timestamp
                    };
                    db.StockLots.Add(lot);
                    db.InventoryBalances.Add(new InventoryBalance { StockLotId = lot.Id, OnHand = quantity, Reserved = 0 });
                    db.StockMovements.Add(new StockMovement
                    {
                        StockLotId = lot.Id, GoodsReceiptItemId = item.Id, Type = StockMovementType.Receipt,
                        OnHandDelta = quantity, ReservedDelta = 0, PostedAtUtc = timestamp
                    });
                    db.QualityInspections.Add(new QualityInspection
                    {
                        StockLotId = lot.Id, DepartmentId = request.QualityDepartmentId,
                        Status = QualityInspectionStatus.Pending
                    });
                    received[orderLine.Id] = priorQuantity + quantity;
                }
                order.Status = order.Items.All(x => received.GetValueOrDefault(x.Id) + x.CancelledQuantity == x.Quantity)
                    ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
                // Even a second partial receipt must advance the parent concurrency token.
                db.Entry(order).Property(x => x.Status).IsModified = true;
                await db.SaveChangesAsync();
                var view = await ReceiptViewAsync(db, receipt.Id);
                return new CommandResult<ReceiptView>(view, nameof(GoodsReceipt), receipt.Id, receipt.DepartmentId,
                    "Posted", new { Receipt = view, PurchaseOrderStatus = order.Status, QualityDepartmentId = request.QualityDepartmentId }, 201);
            }));
        return endpoints;
    }

    private static async Task<IResult> ListReceipts(StockFlowDbContext db, AccessService access,
        string? search = null, Guid? departmentId = null, Guid? purchaseOrderId = null,
        DateTime? postedFromUtc = null, DateTime? postedToUtc = null, int page = 1, int pageSize = 20)
    {
        var scope = await access.ScopeAsync("Receipts.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Permission is required for this scope.");
        CatalogEndpoints.ValidatePage(page, pageSize);
        if (postedFromUtc > postedToUtc) throw new ApiException(400, "Posted date range is invalid.");
        var query = db.GoodsReceipts.AsNoTracking()
            .Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId));
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        if (purchaseOrderId.HasValue) query = query.Where(x => x.PurchaseOrderId == purchaseOrderId.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Number.Contains(search) || x.PurchaseOrder.Number.Contains(search));
        }
        if (postedFromUtc.HasValue) query = query.Where(x => x.PostedAtUtc >= postedFromUtc.Value);
        if (postedToUtc.HasValue) query = query.Where(x => x.PostedAtUtc <= postedToUtc.Value);
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.PostedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Number, x.DepartmentId, x.PurchaseOrderId,
                PurchaseOrderNumber = x.PurchaseOrder.Number, x.PostedByUserId, x.PostedAtUtc, LineCount = x.Items.Count })
            .ToListAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<ReceiptView> ReceiptViewAsync(StockFlowDbContext db, Guid id)
    {
        var receipt = await db.GoodsReceipts.AsNoTracking().Include(x => x.PurchaseOrder)
            .SingleAsync(x => x.Id == id);
        var lines = await (from item in db.GoodsReceiptItems.AsNoTracking()
            join lot in db.StockLots on item.Id equals lot.GoodsReceiptItemId
            join inspection in db.QualityInspections on lot.Id equals inspection.StockLotId
            where item.GoodsReceiptId == id
            orderby item.Id
            select new ReceiptLineView(item.Id, item.PurchaseOrderItemId, item.PurchaseOrderItem.ProductId,
                item.PurchaseOrderItem.Product.Sku, item.PurchaseOrderItem.Product.Name,
                item.PurchaseOrderItem.Product.BaseUnit, item.ReceivedQuantity, lot.Id, lot.LotNumber,
                lot.QualityStatus, inspection.Id)).ToListAsync();
        return new ReceiptView(receipt.Id, receipt.Number, receipt.DepartmentId, receipt.PurchaseOrderId,
            receipt.PurchaseOrder.Number, receipt.PostedByUserId, receipt.PostedAtUtc, receipt.PurchaseOrder.Version, lines);
    }

    public sealed record ReceiptRequest(Guid RequestId, string Number, Guid DepartmentId, Guid QualityDepartmentId,
        Guid PurchaseOrderId, byte[]? PurchaseOrderVersion, List<ReceiptLineRequest>? Items);
    public sealed record ReceiptLineRequest(Guid PurchaseOrderItemId, decimal ReceivedQuantity, string LotNumber);
    public sealed record ReceiptView(Guid Id, string Number, Guid DepartmentId, Guid PurchaseOrderId,
        string PurchaseOrderNumber, Guid PostedByUserId, DateTime PostedAtUtc, byte[] PurchaseOrderVersion,
        IReadOnlyList<ReceiptLineView> Items);
    public sealed record ReceiptLineView(Guid Id, Guid PurchaseOrderItemId, Guid ProductId, string Sku,
        string ProductName, string BaseUnit, decimal ReceivedQuantity, Guid StockLotId, string LotNumber,
        QualityStatus QualityStatus, Guid QualityInspectionId);
}
