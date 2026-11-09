using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class ProductionEndpoints
{
    public static IEndpointRouteBuilder MapProductionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/production-orders").RequireAuthorization();
        orders.MapGet("/", ListOrders);
        orders.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await RequireOrderPermission(db, access, id, "Production.Read");
            return Results.Ok(await ViewAsync(db, id));
        });
        orders.MapPost("/", (ProductionCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "ProductionOrder.Create", request, async () =>
            {
                await access.RequireAsync("Production.Plan", request.DepartmentId);
                await access.RequireAsync("Bom.Read");
                await RequireSourcePermission(db, access, request.SalesOrderItemId);
            }, async () =>
            {
                var source = await FindSource(db, request.SalesOrderItemId);
                await LockSalesOrder(db, source.SalesOrderId);
                await db.Entry(source.SalesOrder).ReloadAsync();
                var quantity = Input.Quantity(request.PlannedQuantity);
                if (quantity <= 0 || quantity > source.Quantity)
                    throw new ApiException(400, "Planned quantity must be positive and cannot exceed the sales line quantity.");
                ValidateSchedule(request.ScheduledStartUtc, request.ScheduledEndUtc);
                var bom = await FindBom(db, request.BillOfMaterialId);
                await ValidatePlan(db, request.DepartmentId, source, bom);
                var order = new ProductionOrder
                {
                    Number = Input.Code(request.Number, 60), DepartmentId = request.DepartmentId,
                    SalesOrderItemId = source.Id, BillOfMaterialId = bom.Id, PlannedQuantity = quantity,
                    ScheduledStartUtc = request.ScheduledStartUtc, ScheduledEndUtc = request.ScheduledEndUtc,
                    Status = ProductionOrderStatus.Draft,
                    Requirements = bom.Items.Select(x => new ProductionRequirement
                    {
                        MaterialProductId = x.MaterialProductId, RequiredQuantity = RequiredQuantity(quantity, x.QuantityPerUnit)
                    }).ToList()
                };
                db.ProductionOrders.Add(order);
                await db.SaveChangesAsync();
                return await Result(db, order, "ProductionOrder.Created", 201);
            }));
        orders.MapPost("/{id:guid}/release", (Guid id, ProductionTransition request, StockFlowDbContext db,
            AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "ProductionOrder.Release", new { id, request }, async () =>
            {
                await RequireOrderPermission(db, access, id, "Production.Plan");
                await access.RequireAsync("Bom.Read", conceal: true);
                var sourceId = await db.ProductionOrders.Where(x => x.Id == id).Select(x => x.SalesOrderItemId).SingleAsync();
                await RequireSourcePermission(db, access, sourceId);
            }, async () =>
            {
                var order = await FindLockedOrder(db, id);
                RequireVersion(order, request.Version);
                if (order.Status != ProductionOrderStatus.Draft)
                    throw new ApiException(409, "Only a Draft production order can be released.");
                var source = await FindSource(db, order.SalesOrderItemId);
                var bom = await FindBom(db, order.BillOfMaterialId);
                await ValidatePlan(db, order.DepartmentId, source, bom);
                if (order.Requirements.Count != bom.Items.Count || bom.Items.Any(item =>
                        !order.Requirements.Any(requirement => requirement.MaterialProductId == item.MaterialProductId
                            && requirement.RequiredQuantity == RequiredQuantity(order.PlannedQuantity, item.QuantityPerUnit))))
                    throw new ApiException(409, "The snapshotted requirements do not match the approved BOM.");
                order.Status = ProductionOrderStatus.Released;
                await db.SaveChangesAsync();
                return await Result(db, order, "ProductionOrder.Released");
            }));
        orders.MapPost("/{id:guid}/cancel", (Guid id, ProductionTransition request, StockFlowDbContext db,
            AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "ProductionOrder.Cancel", new { id, request },
                () => RequireOrderPermission(db, access, id, "Production.Plan"), async () =>
                {
                    var order = await FindLockedOrder(db, id);
                    RequireVersion(order, request.Version);
                    if (order.Status is not (ProductionOrderStatus.Draft or ProductionOrderStatus.Released)
                        || await db.MaterialIssues.AnyAsync(x => x.ProductionOrderId == id))
                        throw new ApiException(409, "Only an unstarted production order can be cancelled.");
                    order.Status = ProductionOrderStatus.Cancelled;
                    await db.SaveChangesAsync();
                    return await Result(db, order, "ProductionOrder.Cancelled");
                }));
        endpoints.MapGet("/api/production-demand", ListDemand).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListOrders(StockFlowDbContext db, AccessService access,
        Guid? departmentId = null, Guid? salesOrderItemId = null, ProductionOrderStatus? status = null,
        string? search = null, int page = 1, int pageSize = 20)
    {
        var scope = await access.ScopeAsync("Production.Read");
        RequireCollectionScope(scope);
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.ProductionOrders.AsNoTracking()
            .Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId));
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        if (salesOrderItemId.HasValue) query = query.Where(x => x.SalesOrderItemId == salesOrderItemId.Value);
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid production order status.");
            query = query.Where(x => x.Status == status.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Number.Contains(search) || x.SalesOrderItem.Product.Sku.Contains(search)
                || x.SalesOrderItem.SalesOrder.Number.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id, x.Number, x.DepartmentId, x.SalesOrderItemId,
                SalesOrderNumber = x.SalesOrderItem.SalesOrder.Number, x.SalesOrderItem.ProductId,
                x.SalesOrderItem.Product.Sku, x.PlannedQuantity, x.Status,
                ScheduledStartUtc = DateTime.SpecifyKind(x.ScheduledStartUtc, DateTimeKind.Utc),
                ScheduledEndUtc = DateTime.SpecifyKind(x.ScheduledEndUtc, DateTimeKind.Utc),
                CreatedAtUtc = DateTime.SpecifyKind(x.CreatedAtUtc, DateTimeKind.Utc), x.Version
            }).ToListAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task<IResult> ListDemand(StockFlowDbContext db, AccessService access,
        Guid? departmentId = null, Guid? productId = null, string? search = null, int page = 1, int pageSize = 20)
    {
        var salesScope = await access.ScopeAsync("Orders.Read");
        RequireCollectionScope(salesScope);
        var workScope = await access.ScopeAsync("Production.Read");
        RequireCollectionScope(workScope);
        await access.RequireAsync("Inventory.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.SalesOrderItems.AsNoTracking().Where(x => x.SalesOrder.Status == SalesOrderStatus.Confirmed
            && (salesScope.CompanyWide || salesScope.DepartmentIds.Contains(x.SalesOrder.DepartmentId)));
        if (departmentId.HasValue) query = query.Where(x => x.SalesOrder.DepartmentId == departmentId.Value);
        if (productId.HasValue) query = query.Where(x => x.ProductId == productId.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.SalesOrder.Number.Contains(search) || x.Product.Sku.Contains(search)
                || x.Product.Name.Contains(search));
        }
        var total = await query.CountAsync();
        var pageQuery = query.OrderBy(x => x.SalesOrder.RequiredByUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize);
        var work = db.ProductionOrders.AsNoTracking()
            .Where(x => workScope.CompanyWide || workScope.DepartmentIds.Contains(x.DepartmentId));
        var outputs = db.ProductionOutputs.AsNoTracking().Where(x =>
            workScope.CompanyWide || workScope.DepartmentIds.Contains(x.ProductionOrder.DepartmentId));
        var items = await pageQuery.Select(x => new
        {
            SalesOrderItemId = x.Id, x.SalesOrderId, SalesOrderNumber = x.SalesOrder.Number,
            x.SalesOrder.DepartmentId,
            RequiredByUtc = DateTime.SpecifyKind(x.SalesOrder.RequiredByUtc, DateTimeKind.Utc), x.ProductId,
            x.Product.Sku, ProductName = x.Product.Name, x.Product.BaseUnit, DemandQuantity = x.Quantity,
            ReleasedAvailableQuantity = db.InventoryBalances.Where(b => b.StockLot.ProductId == x.ProductId
                && b.StockLot.QualityStatus == QualityStatus.Released).Sum(b => (decimal?)(b.OnHand - b.Reserved)) ?? 0,
            VisiblePlannedQuantity = work.Where(w => w.SalesOrderItemId == x.Id && w.Status != ProductionOrderStatus.Cancelled)
                .Sum(w => (decimal?)w.PlannedQuantity) ?? 0,
            VisibleProducedQuantity = outputs.Where(o => o.ProductionOrder.SalesOrderItemId == x.Id)
                .Sum(o => (decimal?)o.ProducedQuantity) ?? 0,
            VisibleScrapQuantity = outputs.Where(o => o.ProductionOrder.SalesOrderItemId == x.Id)
                .Sum(o => (decimal?)o.ScrapQuantity) ?? 0,
            VisibleRejectedOutputQuantity = outputs.Where(o => o.ProductionOrder.SalesOrderItemId == x.Id
                && db.StockLots.Any(l => l.ProductionOutputId == o.Id && l.QualityStatus == QualityStatus.Rejected))
                .Sum(o => (decimal?)o.ProducedQuantity) ?? 0,
            VisibleDraftCount = work.Count(w => w.SalesOrderItemId == x.Id && w.Status == ProductionOrderStatus.Draft),
            VisibleReleasedCount = work.Count(w => w.SalesOrderItemId == x.Id && w.Status == ProductionOrderStatus.Released),
            VisibleInProductionCount = work.Count(w => w.SalesOrderItemId == x.Id && w.Status == ProductionOrderStatus.InProduction),
            VisibleAwaitingQcCount = work.Count(w => w.SalesOrderItemId == x.Id && w.Status == ProductionOrderStatus.AwaitingQC),
            VisibleCompletedCount = work.Count(w => w.SalesOrderItemId == x.Id && w.Status == ProductionOrderStatus.Completed),
            VisibleCancelledCount = work.Count(w => w.SalesOrderItemId == x.Id && w.Status == ProductionOrderStatus.Cancelled)
        }).ToListAsync();
        return Results.Ok(new { items, total, page, pageSize, workTotalsScope = "Visible production departments",
            stockIsSharedAndUnallocated = true });
    }

    internal static async Task<ProductionOrderView> ViewAsync(StockFlowDbContext db, Guid id)
    {
        var order = await db.ProductionOrders.AsNoTracking()
            .Include(x => x.SalesOrderItem).ThenInclude(x => x.SalesOrder)
            .Include(x => x.SalesOrderItem).ThenInclude(x => x.Product)
            .Include(x => x.BillOfMaterial).Include(x => x.Requirements).ThenInclude(x => x.MaterialProduct)
            .SingleOrDefaultAsync(x => x.Id == id) ?? throw new ApiException(404, "Resource not found.");
        var issued = await db.MaterialIssueItems.Where(x => x.MaterialIssue.ProductionOrderId == id)
            .GroupBy(x => x.ProductionRequirementId)
            .Select(x => new { Id = x.Key, Quantity = x.Sum(item => item.Quantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity);
        var issueId = await db.MaterialIssues.Where(x => x.ProductionOrderId == id).Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
        var output = await db.ProductionOutputs.AsNoTracking().SingleOrDefaultAsync(x => x.ProductionOrderId == id);
        var outputLot = output is null ? null : await db.StockLots.AsNoTracking().SingleOrDefaultAsync(x => x.ProductionOutputId == output.Id);
        var product = order.SalesOrderItem.Product;
        return new ProductionOrderView(order.Id, order.Number, order.DepartmentId, order.SalesOrderItemId,
            order.SalesOrderItem.SalesOrderId, order.SalesOrderItem.SalesOrder.Number, product.Id, product.Sku,
            product.Name, product.BaseUnit, order.BillOfMaterialId, order.BillOfMaterial.Revision, order.PlannedQuantity,
            DateTime.SpecifyKind(order.ScheduledStartUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(order.ScheduledEndUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(order.CreatedAtUtc, DateTimeKind.Utc), order.Status, order.Version,
            order.Requirements.OrderBy(x => x.MaterialProduct.Sku).ThenBy(x => x.Id).Select(x => new RequirementView(
                x.Id, x.MaterialProductId, x.MaterialProduct.Sku, x.MaterialProduct.Name, x.MaterialProduct.BaseUnit,
                x.RequiredQuantity, issued.GetValueOrDefault(x.Id))).ToArray(), issueId,
            output is null ? null : new OutputView(output.Id, output.ProducedQuantity, output.ScrapQuantity,
                DateTime.SpecifyKind(output.PostedAtUtc, DateTimeKind.Utc), outputLot?.Id, outputLot?.QualityStatus));
    }

    private static async Task<ProductionOrder> FindLockedOrder(StockFlowDbContext db, Guid id)
    {
        var salesId = await db.ProductionOrders.AsNoTracking().Where(x => x.Id == id)
            .Select(x => (Guid?)x.SalesOrderItem.SalesOrderId).SingleOrDefaultAsync()
            ?? throw new ApiException(404, "Resource not found.");
        await LockSalesOrder(db, salesId);
        return await db.ProductionOrders.FromSqlInterpolated($"SELECT * FROM [ProductionOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .Include(x => x.Requirements).SingleAsync();
    }

    private static async Task LockSalesOrder(StockFlowDbContext db, Guid id)
    {
        await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM [SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleAsync();
    }

    private static async Task RequireOrderPermission(StockFlowDbContext db, AccessService access, Guid id, string permission)
    {
        var scope = await access.ScopeAsync(permission);
        if (!await db.ProductionOrders.AnyAsync(x => x.Id == id
                && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private static async Task RequireSourcePermission(StockFlowDbContext db, AccessService access, Guid id)
    {
        var scope = await access.ScopeAsync("Orders.Read");
        if (!await db.SalesOrderItems.AnyAsync(x => x.Id == id
                && (scope.CompanyWide || scope.DepartmentIds.Contains(x.SalesOrder.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private static async Task<SalesOrderItem> FindSource(StockFlowDbContext db, Guid id) =>
        await db.SalesOrderItems.Include(x => x.SalesOrder).Include(x => x.Product)
            .SingleOrDefaultAsync(x => x.Id == id) ?? throw new ApiException(404, "Resource not found.");

    private static async Task<BillOfMaterial> FindBom(StockFlowDbContext db, Guid id) =>
        await db.BillOfMaterials.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ApiException(400, "An approved matching BOM is required.");

    private static async Task ValidatePlan(StockFlowDbContext db, Guid departmentId, SalesOrderItem source, BillOfMaterial bom)
    {
        if (source.SalesOrder.Status != SalesOrderStatus.Confirmed)
            throw new ApiException(409, "Production requires a Confirmed sales order.");
        if (!await db.Departments.AnyAsync(x => x.Id == departmentId && x.IsActive))
            throw new ApiException(400, "An active executing department is required.");
        if (!source.Product.IsActive || source.Product.ItemType != ItemType.FinishedGood
            || bom.FinishedProductId != source.ProductId || bom.Status != BomStatus.Approved)
            throw new ApiException(409, "An active finished good and its approved BOM are required.");
        if (bom.Items.Count is < 1 or > 20 || bom.Items.Select(x => x.MaterialProductId).Distinct().Count() != bom.Items.Count)
            throw new ApiException(409, "The BOM must contain 1 to 20 distinct materials.");
        var materials = bom.Items.Select(x => x.MaterialProductId).ToArray();
        if (await db.Products.CountAsync(x => materials.Contains(x.Id) && x.IsActive && x.ItemType == ItemType.RawMaterial) != materials.Length)
            throw new ApiException(409, "Every BOM input must be an active raw material.");
    }

    private static decimal RequiredQuantity(decimal planned, decimal ratio)
    {
        if (Input.Quantity(ratio) <= 0) throw new ApiException(400, "BOM quantities must be positive.");
        try
        {
            var amount = planned * ratio;
            if (amount > 999999999999999.999m)
                throw new ApiException(400, "The calculated material requirement exceeds decimal(18,3).");
            return Input.Quantity(decimal.Ceiling(amount * 1000m) / 1000m);
        }
        catch (OverflowException)
        {
            throw new ApiException(400, "The calculated material requirement exceeds decimal(18,3).");
        }
    }

    private static void ValidateSchedule(DateTime start, DateTime end)
    {
        if (start == default || end == default || start.Kind != DateTimeKind.Utc || end.Kind != DateTimeKind.Utc || end < start)
            throw new ApiException(400, "Provide UTC schedule timestamps with an end at or after the start.");
    }

    private static void RequireVersion(ProductionOrder order, byte[]? version)
    {
        if (version is not { Length: 8 } || !order.Version.SequenceEqual(version))
            throw new ApiException(409, "The production order changed. Reload and supply its current Version.");
    }

    private static void RequireCollectionScope(AccessScope scope)
    {
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Permission is required for this scope.");
    }

    private static async Task<CommandResult<ProductionOrderView>> Result(StockFlowDbContext db, ProductionOrder order,
        string action, int status = 200)
    {
        var view = await ViewAsync(db, order.Id);
        return new CommandResult<ProductionOrderView>(view, nameof(ProductionOrder), order.Id, order.DepartmentId, action, view, status);
    }

    public sealed record ProductionCreate(Guid RequestId, string Number, Guid DepartmentId, Guid SalesOrderItemId,
        Guid BillOfMaterialId, decimal PlannedQuantity, DateTime ScheduledStartUtc, DateTime ScheduledEndUtc);
    public sealed record ProductionTransition(Guid RequestId, byte[]? Version);
    public sealed record RequirementView(Guid Id, Guid MaterialProductId, string Sku, string MaterialName,
        string BaseUnit, decimal RequiredQuantity, decimal IssuedQuantity);
    public sealed record OutputView(Guid Id, decimal ProducedQuantity, decimal ScrapQuantity, DateTime PostedAtUtc,
        Guid? StockLotId, QualityStatus? QualityStatus);
    public sealed record ProductionOrderView(Guid Id, string Number, Guid DepartmentId, Guid SalesOrderItemId,
        Guid SalesOrderId, string SalesOrderNumber, Guid ProductId, string Sku, string ProductName, string BaseUnit,
        Guid BillOfMaterialId, int BomRevision, decimal PlannedQuantity, DateTime ScheduledStartUtc,
        DateTime ScheduledEndUtc, DateTime CreatedAtUtc, ProductionOrderStatus Status, byte[] Version,
        IReadOnlyList<RequirementView> Requirements, Guid? MaterialIssueId, OutputView? Output);
}
