using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var products = endpoints.MapGroup("/api/products").RequireAuthorization();
        products.MapGet("/", ListProducts);
        products.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Products.Read", conceal: true);
            var item = await db.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(ProductView.From(item));
        });
        products.MapPost("/", (ProductRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Product.Create", request,
                () => access.RequireAsync("Products.Manage"), async () =>
                {
                    var item = new Product { Id = Guid.NewGuid() };
                    ApplyProduct(item, request);
                    db.Products.Add(item);
                    await db.SaveChangesAsync();
                    return ProductResult(item, "Product.Created", 201);
                }));
        products.MapPut("/{id:guid}", (Guid id, ProductRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Product.Update", new { id, request },
                () => access.RequireAsync("Products.Manage", conceal: true), async () =>
                {
                    var item = await db.Products.SingleOrDefaultAsync(x => x.Id == id)
                        ?? throw new ApiException(404, "Resource not found.");
                    var sku = Input.Code(request.Sku, 60);
                    var unit = Input.Required(request.BaseUnit, 30);
                    if ((item.Sku != sku || item.ItemType != request.ItemType || item.BaseUnit != unit)
                        && (await db.StockLots.AnyAsync(x => x.ProductId == id)
                            || await db.PurchaseOrderItems.AnyAsync(x => x.ProductId == id)
                            || await db.SalesOrderItems.AnyAsync(x => x.ProductId == id)
                            || await db.BillOfMaterials.AnyAsync(x => x.FinishedProductId == id)
                            || await db.BomItems.AnyAsync(x => x.MaterialProductId == id)))
                        throw new ApiException(409, "SKU, item type, and base unit cannot change after purchasing, sales, BOM, or inventory use.");
                    ApplyProduct(item, request);
                    await db.SaveChangesAsync();
                    return ProductResult(item, "Product.Updated");
                }));

        var customers = endpoints.MapGroup("/api/customers").RequireAuthorization();
        customers.MapGet("/", ListCustomers);
        customers.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Customers.Read", conceal: true);
            var item = await db.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(CustomerView.From(item));
        });
        customers.MapPost("/", (CustomerRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Customer.Create", request,
                () => access.RequireAsync("Customers.Manage"), async () =>
                {
                    var item = new Customer { Id = Guid.NewGuid() };
                    ApplyCustomer(item, request);
                    db.Customers.Add(item);
                    await db.SaveChangesAsync();
                    return CustomerResult(item, "Customer.Created", 201);
                }));
        customers.MapPut("/{id:guid}", (Guid id, CustomerRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Customer.Update", new { id, request },
                () => access.RequireAsync("Customers.Manage", conceal: true), async () =>
                {
                    var item = await db.Customers.SingleOrDefaultAsync(x => x.Id == id)
                        ?? throw new ApiException(404, "Resource not found.");
                    ApplyCustomer(item, request);
                    await db.SaveChangesAsync();
                    return CustomerResult(item, "Customer.Updated");
                }));

        var suppliers = endpoints.MapGroup("/api/suppliers").RequireAuthorization();
        suppliers.MapGet("/", ListSuppliers);
        suppliers.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Suppliers.Read", conceal: true);
            var item = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(SupplierView.From(item));
        });
        suppliers.MapPost("/", (SupplierRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Supplier.Create", request,
                () => access.RequireAsync("Suppliers.Manage"), async () =>
                {
                    var item = new Supplier { Id = Guid.NewGuid() };
                    ApplySupplier(item, request);
                    db.Suppliers.Add(item);
                    await db.SaveChangesAsync();
                    return SupplierResult(item, "Supplier.Created", 201);
                }));
        suppliers.MapPut("/{id:guid}", (Guid id, SupplierRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Supplier.Update", new { id, request },
                () => access.RequireAsync("Suppliers.Manage", conceal: true), async () =>
                {
                    var item = await db.Suppliers.SingleOrDefaultAsync(x => x.Id == id)
                        ?? throw new ApiException(404, "Resource not found.");
                    ApplySupplier(item, request);
                    await db.SaveChangesAsync();
                    return SupplierResult(item, "Supplier.Updated");
                }));
        return endpoints;
    }

    private static async Task<IResult> ListProducts(StockFlowDbContext db, AccessService access,
        string? search = null, bool? active = null, ItemType? itemType = null, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Products.Read");
        ValidatePage(page, pageSize);
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Sku.Contains(search) || x.Name.Contains(search));
        }
        if (active.HasValue) query = query.Where(x => x.IsActive == active.Value);
        if (itemType.HasValue)
        {
            if (!Enum.IsDefined(itemType.Value)) throw new ApiException(400, "Invalid item type.");
            query = query.Where(x => x.ItemType == itemType.Value);
        }
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.Sku).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Results.Ok(new { items = items.Select(ProductView.From), total, page, pageSize });
    }

    private static async Task<IResult> ListCustomers(StockFlowDbContext db, AccessService access,
        string? search = null, bool? active = null, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Customers.Read");
        ValidatePage(page, pageSize);
        var query = db.Customers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
        }
        if (active.HasValue) query = query.Where(x => x.IsActive == active.Value);
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Results.Ok(new { items = items.Select(CustomerView.From), total, page, pageSize });
    }

    private static async Task<IResult> ListSuppliers(StockFlowDbContext db, AccessService access,
        string? search = null, bool? active = null, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Suppliers.Read");
        ValidatePage(page, pageSize);
        var query = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Code.Contains(search) || x.Name.Contains(search));
        }
        if (active.HasValue) query = query.Where(x => x.IsActive == active.Value);
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.Code).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Results.Ok(new { items = items.Select(SupplierView.From), total, page, pageSize });
    }

    internal static void ValidatePage(int page, int pageSize) => Input.ValidatePage(page, pageSize);

    private static void ApplyProduct(Product item, ProductRequest request)
    {
        if (!Enum.IsDefined(request.ItemType)) throw new ApiException(400, "Invalid item type.");
        item.Sku = Input.Code(request.Sku, 60);
        item.Name = Input.Required(request.Name);
        item.ItemType = request.ItemType;
        item.BaseUnit = Input.Required(request.BaseUnit, 30);
        item.SellingPrice = Input.Money(request.SellingPrice);
        item.ReorderLevel = Input.Quantity(request.ReorderLevel);
        item.IsActive = request.IsActive;
    }

    private static void ApplyCustomer(Customer item, CustomerRequest request)
    {
        item.Code = Input.Code(request.Code, 60);
        item.Name = Input.Required(request.Name);
        item.DeliveryAddress = Input.Required(request.DeliveryAddress, 1000);
        item.IsActive = request.IsActive;
    }

    private static void ApplySupplier(Supplier item, SupplierRequest request)
    {
        item.Code = Input.Code(request.Code, 60);
        item.Name = Input.Required(request.Name);
        item.IsActive = request.IsActive;
    }

    private static CommandResult<ProductView> ProductResult(Product item, string action, int status = 200) =>
        new(ProductView.From(item), nameof(Product), item.Id, null, action, ProductView.From(item), status);
    private static CommandResult<CustomerView> CustomerResult(Customer item, string action, int status = 200) =>
        new(CustomerView.From(item), nameof(Customer), item.Id, null, action, CustomerView.From(item), status);
    private static CommandResult<SupplierView> SupplierResult(Supplier item, string action, int status = 200) =>
        new(SupplierView.From(item), nameof(Supplier), item.Id, null, action, SupplierView.From(item), status);

    public sealed record ProductRequest(Guid RequestId, string Sku, string Name, ItemType ItemType,
        string BaseUnit, decimal SellingPrice, decimal ReorderLevel, bool IsActive = true);
    public sealed record CustomerRequest(Guid RequestId, string Code, string Name, string DeliveryAddress, bool IsActive = true);
    public sealed record SupplierRequest(Guid RequestId, string Code, string Name, bool IsActive = true);
    public sealed record ProductView(Guid Id, string Sku, string Name, ItemType ItemType, string BaseUnit,
        decimal SellingPrice, decimal ReorderLevel, bool IsActive)
    {
        public static ProductView From(Product item) => new(item.Id, item.Sku, item.Name, item.ItemType,
            item.BaseUnit, item.SellingPrice, item.ReorderLevel, item.IsActive);
    }
    public sealed record CustomerView(Guid Id, string Code, string Name, string DeliveryAddress, bool IsActive)
    {
        public static CustomerView From(Customer item) => new(item.Id, item.Code, item.Name, item.DeliveryAddress, item.IsActive);
    }
    public sealed record SupplierView(Guid Id, string Code, string Name, bool IsActive)
    {
        public static SupplierView From(Supplier item) => new(item.Id, item.Code, item.Name, item.IsActive);
    }
}


