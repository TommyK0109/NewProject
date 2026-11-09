using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class SalesEndpoints
{
    public static IEndpointRouteBuilder MapSalesEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/sales-orders").RequireAuthorization();
        orders.MapGet("/", ListOrders);
        orders.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await RequirePermission(db, access, id, "Orders.Read");
            return Results.Ok(await View(db, await FindOrder(db, id)));
        });
        orders.MapPost("/", (SalesOrderCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "SalesOrder.Create", request,
                () => access.RequireAsync("Orders.Create", request.DepartmentId), async () =>
                {
                    var products = await ValidateDraft(db, request.DepartmentId, request.CustomerId, request.RequiredByUtc, request.Items);
                    var order = new SalesOrder
                    {
                        Id = Guid.NewGuid(), Number = Input.Code(request.Number, 60), DepartmentId = request.DepartmentId,
                        CustomerId = request.CustomerId, RequiredByUtc = request.RequiredByUtc, Status = SalesOrderStatus.Draft
                    };
                    ApplyItems(db, order, request.Items!, products);
                    db.SalesOrders.Add(order);
                    await db.SaveChangesAsync();
                    return await Result(db, order, "SalesOrder.Created", 201);
                }));
        orders.MapPut("/{id:guid}", (Guid id, SalesOrderUpdate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "SalesOrder.Update", new { id, request },
                () => RequirePermission(db, access, id, "Orders.Create"), async () =>
                {
                    var order = await FindOrder(db, id);
                    RequireVersion(order, request.Version);
                    if (order.Status != SalesOrderStatus.Draft)
                        throw new ApiException(409, "Only a Draft sales order can be edited.");
                    var products = await ValidateDraft(db, order.DepartmentId, request.CustomerId, request.RequiredByUtc, request.Items);
                    order.Number = Input.Code(request.Number, 60);
                    order.CustomerId = request.CustomerId;
                    order.RequiredByUtc = request.RequiredByUtc;
                    ApplyItems(db, order, request.Items!, products);
                    // A line-only edit must also advance the header's rowversion.
                    db.Entry(order).Property(x => x.CustomerId).IsModified = true;
                    await db.SaveChangesAsync();
                    return await Result(db, order, "SalesOrder.Updated");
                }));
        orders.MapPost("/{id:guid}/confirm", (Guid id, SalesOrderConfirm request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "SalesOrder.Confirm", new { id, request },
                () => RequirePermission(db, access, id, "Orders.Confirm"), async () =>
                {
                    var order = await FindOrder(db, id);
                    RequireVersion(order, request.Version);
                    if (order.Status != SalesOrderStatus.Draft)
                        throw new ApiException(409, "Only a Draft sales order can be confirmed.");
                    await ValidateDraft(db, order.DepartmentId, order.CustomerId, DateTime.SpecifyKind(order.RequiredByUtc, DateTimeKind.Utc),
                        order.Items.Select(x => new SalesOrderLine(x.ProductId, x.Quantity)).ToArray());
                    // Confirmation preserves the draft's saved selling prices and does not reserve stock.
                    order.Status = SalesOrderStatus.Confirmed;
                    await db.SaveChangesAsync();
                    return await Result(db, order, "SalesOrder.Confirmed");
                }));
        return endpoints;
    }

    private static async Task<IResult> ListOrders(StockFlowDbContext db, AccessService access,
        Guid? departmentId = null, Guid? customerId = null, SalesOrderStatus? status = null,
        string? search = null, int page = 1, int pageSize = 20)
    {
        var scope = await access.ScopeAsync("Orders.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Permission is required for this scope.");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.SalesOrders.AsNoTracking()
            .Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId));
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid sales order status.");
            query = query.Where(x => x.Status == status.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Number.Contains(search) || x.Customer.Name.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.RequiredByUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new SalesOrderSummary(x.Id, x.Number, x.DepartmentId, x.CustomerId, x.Status,
                x.RequiredByUtc, x.CreatedAtUtc, x.Version, x.Items.Count)).ToListAsync();
        return Results.Ok(new
        {
            items = items.Select(x => x with
            {
                RequiredByUtc = DateTime.SpecifyKind(x.RequiredByUtc, DateTimeKind.Utc),
                CreatedAtUtc = DateTime.SpecifyKind(x.CreatedAtUtc, DateTimeKind.Utc)
            }),
            total, page, pageSize
        });
    }

    private static async Task RequirePermission(StockFlowDbContext db, AccessService access, Guid id, string permission)
    {
        var scope = await access.ScopeAsync(permission);
        if (!await db.SalesOrders.AnyAsync(x => x.Id == id
            && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private static async Task<SalesOrder> FindOrder(StockFlowDbContext db, Guid id) =>
        await db.SalesOrders.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ApiException(404, "Resource not found.");

    private static void RequireVersion(SalesOrder order, byte[]? version)
    {
        if (version is not { Length: 8 } || !order.Version.SequenceEqual(version))
            throw new ApiException(409, "The sales order changed. Reload and supply its current Version.");
    }

    private static async Task<Dictionary<Guid, Product>> ValidateDraft(StockFlowDbContext db, Guid departmentId,
        Guid customerId, DateTime requiredByUtc, SalesOrderLine[]? items)
    {
        if (!await db.Departments.AnyAsync(x => x.Id == departmentId && x.IsActive))
            throw new ApiException(400, "An active owning department is required.");
        if (!await db.Customers.AnyAsync(x => x.Id == customerId && x.IsActive))
            throw new ApiException(400, "An active customer is required.");
        if (requiredByUtc == default || requiredByUtc.Kind != DateTimeKind.Utc)
            throw new ApiException(400, "RequiredByUtc must be a date and time explicitly expressed in UTC (Z).");
        if (items is null || items.Length is < 1 or > 20 || items.Any(x => x is null))
            throw new ApiException(400, "A sales order requires 1–20 lines.");
        if (items.Select(x => x.ProductId).Distinct().Count() != items.Length)
            throw new ApiException(400, "A product can appear only once in a sales order.");
        foreach (var item in items)
            if (Input.Quantity(item.Quantity) == 0)
                throw new ApiException(400, "Sales quantities must be positive.");
        var productIds = items.Select(x => x.ProductId).ToArray();
        var products = await db.Products.AsNoTracking()
            .Where(x => productIds.Contains(x.Id) && x.IsActive && x.ItemType == ItemType.FinishedGood)
            .ToDictionaryAsync(x => x.Id);
        if (products.Count != items.Length)
            throw new ApiException(400, "Every line must reference an active finished good.");
        return products;
    }

    private static void ApplyItems(StockFlowDbContext db, SalesOrder order, SalesOrderLine[] items, Dictionary<Guid, Product> products)
    {
        var productIds = items.Select(x => x.ProductId).ToHashSet();
        foreach (var removed in order.Items.Where(x => !productIds.Contains(x.ProductId)).ToArray())
        {
            order.Items.Remove(removed);
            db.SalesOrderItems.Remove(removed);
        }
        foreach (var line in items)
        {
            var item = order.Items.SingleOrDefault(x => x.ProductId == line.ProductId);
            if (item is null)
            {
                item = new SalesOrderItem { Id = Guid.NewGuid(), SalesOrderId = order.Id, ProductId = line.ProductId };
                order.Items.Add(item);
                db.SalesOrderItems.Add(item);
            }
            item.Quantity = line.Quantity;
            item.UnitPrice = products[line.ProductId].SellingPrice;
        }
    }

    private static async Task<SalesOrderView> View(StockFlowDbContext db, SalesOrder order)
    {
        var productIds = order.Items.Select(x => x.ProductId).ToArray();
        var products = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        return new SalesOrderView(order.Id, order.Number, order.DepartmentId, order.CustomerId, order.Status,
            DateTime.SpecifyKind(order.RequiredByUtc, DateTimeKind.Utc),
            DateTime.SpecifyKind(order.CreatedAtUtc, DateTimeKind.Utc), order.Version,
            order.Items.OrderBy(x => products[x.ProductId].Sku).ThenBy(x => x.Id).Select(x => new SalesOrderLineView(
                x.Id, x.ProductId, products[x.ProductId].Sku, products[x.ProductId].Name, products[x.ProductId].BaseUnit,
                x.Quantity, x.UnitPrice)).ToArray());
    }

    private static async Task<CommandResult<SalesOrderView>> Result(StockFlowDbContext db, SalesOrder order, string action, int status = 200)
    {
        var view = await View(db, order);
        return new CommandResult<SalesOrderView>(view, nameof(SalesOrder), order.Id, order.DepartmentId, action, view, status);
    }

    public sealed record SalesOrderLine(Guid ProductId, decimal Quantity);
    public sealed record SalesOrderCreate(Guid RequestId, string Number, Guid DepartmentId, Guid CustomerId,
        DateTime RequiredByUtc, SalesOrderLine[]? Items);
    public sealed record SalesOrderUpdate(Guid RequestId, string Number, Guid CustomerId,
        DateTime RequiredByUtc, SalesOrderLine[]? Items, byte[]? Version);
    public sealed record SalesOrderConfirm(Guid RequestId, byte[]? Version);
    public sealed record SalesOrderSummary(Guid Id, string Number, Guid DepartmentId, Guid CustomerId, SalesOrderStatus Status,
        DateTime RequiredByUtc, DateTime CreatedAtUtc, byte[] Version, int LineCount);
    public sealed record SalesOrderLineView(Guid Id, Guid ProductId, string Sku, string Name, string BaseUnit, decimal Quantity, decimal UnitPrice);
    public sealed record SalesOrderView(Guid Id, string Number, Guid DepartmentId, Guid CustomerId, SalesOrderStatus Status,
        DateTime RequiredByUtc, DateTime CreatedAtUtc, byte[] Version, SalesOrderLineView[] Items);
}
