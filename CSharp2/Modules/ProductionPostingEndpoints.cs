using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class ProductionPostingEndpoints
{
    public static IEndpointRouteBuilder MapProductionPostingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var production = endpoints.MapGroup("/api/production-orders").RequireAuthorization();
        production.MapPost("/{id:guid}/issue-materials", (Guid id, MaterialIssueRequest request,
            StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "ProductionOrder.IssueMaterials", new { id, request }, async () =>
            {
                await access.RequireAsync("Inventory.Issue");
                await RequireWorkAccess(db, access, id, "Production.Read");
            }, async () =>
            {
                var (salesOrder, work) = await LockWork(db, id);
                RequireVersion(work, request.Version);
                if (work.Status != ProductionOrderStatus.Released || salesOrder.Status != SalesOrderStatus.Confirmed)
                    throw new ApiException(409, "Materials can be issued only for released work against confirmed sales demand.");
                if (await db.MaterialIssues.AnyAsync(x => x.ProductionOrderId == id))
                    throw new ApiException(409, "Materials have already been issued for this production order.");
                if (request.Allocations is null || request.Allocations.Count is < 1 or > 200
                    || request.Allocations.Any(x => x is null)
                    || request.Allocations.Select(x => new { x.ProductionRequirementId, x.StockLotId }).Distinct().Count() != request.Allocations.Count)
                    throw new ApiException(400, "Provide 1 to 200 distinct requirement and lot allocations.");
                if (work.Requirements.Count == 0)
                    throw new ApiException(409, "The production order has no material requirements.");
                var requirements = work.Requirements.ToDictionary(x => x.Id);
                foreach (var allocation in request.Allocations)
                {
                    if (!requirements.ContainsKey(allocation.ProductionRequirementId))
                        throw new ApiException(400, "Every allocation must belong to this production order's requirements.");
                    if (Input.Quantity(allocation.Quantity) <= 0)
                        throw new ApiException(400, "Material issue quantities must be positive.");
                }
                var covered = request.Allocations.GroupBy(x => x.ProductionRequirementId)
                    .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity));
                if (work.Requirements.Any(x => covered.GetValueOrDefault(x.Id) != x.RequiredQuantity))
                    throw new ApiException(409, "Every material requirement must be covered in full in the same issue.");

                var lots = new Dictionary<Guid, StockLot>();
                foreach (var allocationGroup in request.Allocations.GroupBy(x => x.StockLotId).OrderBy(x => x.Key))
                {
                    var lotId = allocationGroup.Key;
                    var lot = await db.StockLots.FromSqlInterpolated($"SELECT * FROM [StockLots] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {lotId}")
                        .Include(x => x.Product).SingleOrDefaultAsync()
                        ?? throw new ApiException(404, "Resource not found.");
                    if (lot.QualityStatus != QualityStatus.Released || lot.Product.ItemType != ItemType.RawMaterial)
                        throw new ApiException(409, "Only released raw-material lots can be issued to production.");
                    if (allocationGroup.Any(x => requirements[x.ProductionRequirementId].MaterialProductId != lot.ProductId))
                        throw new ApiException(400, "The allocated lot's product does not match the material requirement.");
                    var balance = await db.InventoryBalances.FromSqlInterpolated($"SELECT * FROM [InventoryBalances] WITH (UPDLOCK, HOLDLOCK) WHERE [StockLotId] = {lotId}")
                        .SingleOrDefaultAsync()
                        ?? throw new ApiException(409, "The allocated lot has no inventory balance.");
                    var quantity = allocationGroup.Sum(x => x.Quantity);
                    if (quantity > balance.OnHand - balance.Reserved)
                        throw new ApiException(409, "An allocated lot has insufficient available quantity. No materials were issued.");
                    balance.OnHand -= quantity;
                    lots.Add(lotId, lot);
                }

                var timestamp = await DatabaseTime(db);
                var issue = new MaterialIssue
                {
                    ProductionOrderId = work.Id, PostedByUserId = access.UserId, PostedAtUtc = timestamp
                };
                db.MaterialIssues.Add(issue);
                var views = new List<MaterialIssueItemView>();
                foreach (var allocation in request.Allocations.OrderBy(x => x.StockLotId).ThenBy(x => x.ProductionRequirementId))
                {
                    var item = new MaterialIssueItem
                    {
                        MaterialIssue = issue, ProductionRequirementId = allocation.ProductionRequirementId,
                        StockLotId = allocation.StockLotId, Quantity = allocation.Quantity
                    };
                    db.MaterialIssueItems.Add(item);
                    db.StockMovements.Add(new StockMovement
                    {
                        StockLotId = item.StockLotId, MaterialIssueItemId = item.Id,
                        Type = StockMovementType.MaterialIssue, OnHandDelta = -item.Quantity,
                        ReservedDelta = 0, PostedAtUtc = timestamp
                    });
                    var lot = lots[item.StockLotId];
                    views.Add(new MaterialIssueItemView(item.Id, item.ProductionRequirementId, item.StockLotId,
                        lot.LotNumber, lot.ProductId, lot.Product.Sku, lot.Product.BaseUnit, item.Quantity));
                }
                work.Status = ProductionOrderStatus.InProduction;
                await db.SaveChangesAsync();
                var view = new MaterialIssueView(issue.Id, work.Id, work.Status, work.Version,
                    issue.PostedByUserId, issue.PostedAtUtc, views);
                return new CommandResult<MaterialIssueView>(view, nameof(MaterialIssue), issue.Id, work.DepartmentId,
                    "Posted", view, 201);
            }));

        production.MapPost("/{id:guid}/output", (Guid id, OutputRequest request,
            StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "ProductionOrder.PostOutput", new { id, request },
                () => RequireWorkAccess(db, access, id, "Production.Output"), async () =>
                {
                    var (_, work) = await LockWork(db, id);
                    RequireVersion(work, request.Version);
                    if (work.Status != ProductionOrderStatus.InProduction)
                        throw new ApiException(409, "Final output can be posted only for a production order in production.");
                    if (await db.ProductionOutputs.AnyAsync(x => x.ProductionOrderId == id))
                        throw new ApiException(409, "A final result has already been posted for this production order.");
                    var produced = Input.Quantity(request.ProducedQuantity);
                    var scrap = Input.Quantity(request.ScrapQuantity);
                    if (produced + scrap != work.PlannedQuantity)
                        throw new ApiException(400, "Produced quantity plus scrap must equal the planned quantity.");
                    string? lotNumber = null;
                    if (produced > 0)
                    {
                        lotNumber = Input.Code(request.LotNumber, 80);
                        if (!request.QualityDepartmentId.HasValue || !await db.Departments.AnyAsync(x =>
                                x.Id == request.QualityDepartmentId.Value && x.IsActive))
                            throw new ApiException(400, "An active quality department is required for positive output.");
                    }
                    else if (!string.IsNullOrWhiteSpace(request.LotNumber) || request.QualityDepartmentId.HasValue)
                        throw new ApiException(400, "An all-scrap result cannot create a stock lot or inspection.");

                    var timestamp = await DatabaseTime(db);
                    var output = new ProductionOutput
                    {
                        ProductionOrderId = work.Id, ProducedQuantity = produced, ScrapQuantity = scrap,
                        PostedByUserId = access.UserId, PostedAtUtc = timestamp
                    };
                    db.ProductionOutputs.Add(output);
                    StockLot? lot = null;
                    QualityInspection? inspection = null;
                    if (produced > 0)
                    {
                        var productId = work.SalesOrderItem.ProductId;
                        if (!await db.Products.AnyAsync(x => x.Id == productId && x.ItemType == ItemType.FinishedGood))
                            throw new ApiException(409, "The production output must be a finished-good product.");
                        lot = new StockLot
                        {
                            LotNumber = lotNumber!, ProductId = productId, ProductionOutputId = output.Id,
                            QualityStatus = QualityStatus.Pending, ReceivedAtUtc = timestamp
                        };
                        db.StockLots.Add(lot);
                        db.InventoryBalances.Add(new InventoryBalance { StockLotId = lot.Id, OnHand = produced, Reserved = 0 });
                        db.StockMovements.Add(new StockMovement
                        {
                            StockLotId = lot.Id, ProductionOutputId = output.Id, Type = StockMovementType.OutputReceipt,
                            OnHandDelta = produced, ReservedDelta = 0, PostedAtUtc = timestamp
                        });
                        inspection = new QualityInspection
                        {
                            StockLotId = lot.Id, DepartmentId = request.QualityDepartmentId!.Value,
                            Status = QualityInspectionStatus.Pending
                        };
                        db.QualityInspections.Add(inspection);
                        work.Status = ProductionOrderStatus.AwaitingQC;
                    }
                    else work.Status = ProductionOrderStatus.Completed;
                    await db.SaveChangesAsync();
                    var view = new OutputView(output.Id, work.Id, work.Status, work.Version, produced, scrap,
                        output.PostedByUserId, timestamp, lot?.Id, lot?.LotNumber, lot?.QualityStatus, inspection?.Id);
                    return new CommandResult<OutputView>(view, nameof(ProductionOutput), output.Id, work.DepartmentId,
                        "Posted", view, 201);
                }));
        return endpoints;
    }

    private static async Task RequireWorkAccess(StockFlowDbContext db, AccessService access, Guid id, string permission)
    {
        var departmentId = await db.ProductionOrders.Where(x => x.Id == id).Select(x => (Guid?)x.DepartmentId)
            .SingleOrDefaultAsync() ?? throw new ApiException(404, "Resource not found.");
        await access.RequireAsync(permission, departmentId, conceal: true);
    }

    private static async Task<(SalesOrder SalesOrder, ProductionOrder Work)> LockWork(StockFlowDbContext db, Guid id)
    {
        var salesOrderId = await db.ProductionOrders.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (Guid?)x.SalesOrderItem.SalesOrderId).SingleOrDefaultAsync()
            ?? throw new ApiException(404, "Resource not found.");
        // Sales demand precedes work and lot locks in every production mutation.
        var salesOrder = await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM [SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {salesOrderId}")
            .SingleAsync();
        var work = await db.ProductionOrders.FromSqlInterpolated($"SELECT * FROM [ProductionOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .Include(x => x.Requirements).Include(x => x.SalesOrderItem).SingleAsync();
        return (salesOrder, work);
    }

    private static void RequireVersion(ProductionOrder work, byte[]? version)
    {
        if (version is null || version.Length != 8 || !work.Version.SequenceEqual(version))
            throw new ApiException(409, "The production order changed. Reload it and retry.");
    }

    private static async Task<DateTime> DatabaseTime(StockFlowDbContext db) =>
        DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(), DateTimeKind.Utc);

    public sealed record MaterialIssueRequest(Guid RequestId, byte[]? Version, List<MaterialAllocation>? Allocations);
    public sealed record MaterialAllocation(Guid ProductionRequirementId, Guid StockLotId, decimal Quantity);
    public sealed record MaterialIssueView(Guid Id, Guid ProductionOrderId, ProductionOrderStatus Status, byte[] Version,
        Guid PostedByUserId, DateTime PostedAtUtc, IReadOnlyList<MaterialIssueItemView> Items);
    public sealed record MaterialIssueItemView(Guid Id, Guid ProductionRequirementId, Guid StockLotId,
        string LotNumber, Guid MaterialProductId, string Sku, string BaseUnit, decimal Quantity);
    public sealed record OutputRequest(Guid RequestId, byte[]? Version, decimal ProducedQuantity, decimal ScrapQuantity,
        string? LotNumber, Guid? QualityDepartmentId);
    public sealed record OutputView(Guid Id, Guid ProductionOrderId, ProductionOrderStatus Status, byte[] Version,
        decimal ProducedQuantity, decimal ScrapQuantity, Guid PostedByUserId, DateTime PostedAtUtc,
        Guid? StockLotId, string? LotNumber, QualityStatus? QualityStatus, Guid? QualityInspectionId);
}
