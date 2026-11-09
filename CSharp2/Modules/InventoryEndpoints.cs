using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class InventoryEndpoints
{
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var inventory = endpoints.MapGroup("/api/inventory").RequireAuthorization();
        inventory.MapGet("/lots", ListLots);
        inventory.MapGet("/products", ListProducts);
        inventory.MapGet("/lots/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Inventory.Read", conceal: true);
            var lot = await LotViews(db.StockLots.AsNoTracking().Where(x => x.Id == id)).SingleOrDefaultAsync()
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(lot);
        });
        inventory.MapGet("/lots/{id:guid}/movements", async (Guid id, StockFlowDbContext db, AccessService access,
            int page = 1, int pageSize = 20) =>
        {
            await access.RequireAsync("Inventory.Read", conceal: true);
            CatalogEndpoints.ValidatePage(page, pageSize);
            if (!await db.StockLots.AnyAsync(x => x.Id == id)) throw new ApiException(404, "Resource not found.");
            var query = db.StockMovements.AsNoTracking().Where(x => x.StockLotId == id);
            var total = await query.CountAsync();
            var items = await query.OrderBy(x => x.PostedAtUtc).ThenBy(x => x.Id)
                .Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.Id, x.StockLotId, x.Type, x.OnHandDelta, x.ReservedDelta,
                    x.GoodsReceiptItemId, x.ProductionOutputId, x.MaterialIssueItemId,
                    x.StockReservationId, x.ShipmentItemId, x.PostedAtUtc
                }).ToListAsync();
            return Results.Ok(new { items, total, page, pageSize });
        });
        return endpoints;
    }

    private static async Task<IResult> ListLots(StockFlowDbContext db, AccessService access,
        string? search = null, Guid? productId = null, Guid? sourceId = null, QualityStatus? qualityStatus = null,
        DateTime? receivedFromUtc = null, DateTime? receivedToUtc = null, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Inventory.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        if (receivedFromUtc > receivedToUtc) throw new ApiException(400, "Received date range is invalid.");
        var query = db.StockLots.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.LotNumber.Contains(search) || x.Product.Sku.Contains(search)
                || x.Product.Name.Contains(search));
        }
        if (productId.HasValue) query = query.Where(x => x.ProductId == productId.Value);
        if (sourceId.HasValue)
            query = query.Where(x => x.GoodsReceiptItemId == sourceId.Value || x.ProductionOutputId == sourceId.Value);
        if (qualityStatus.HasValue)
        {
            if (!Enum.IsDefined(qualityStatus.Value)) throw new ApiException(400, "Invalid quality status.");
            query = query.Where(x => x.QualityStatus == qualityStatus.Value);
        }
        if (receivedFromUtc.HasValue) query = query.Where(x => x.ReceivedAtUtc >= receivedFromUtc.Value);
        if (receivedToUtc.HasValue) query = query.Where(x => x.ReceivedAtUtc <= receivedToUtc.Value);
        var total = await query.CountAsync();
        var items = await LotViews(query.OrderByDescending(x => x.ReceivedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static IQueryable<LotView> LotViews(IQueryable<StockLot> query) => query.Select(x => new LotView(
        x.Id, x.LotNumber, x.ProductId, x.Product.Sku, x.Product.Name, x.Product.ItemType, x.Product.BaseUnit,
        x.QualityStatus, x.ReceivedAtUtc, x.GoodsReceiptItemId, x.ProductionOutputId,
        x.Balance.OnHand, x.Balance.Reserved,
        x.QualityStatus == QualityStatus.Released ? x.Balance.OnHand - x.Balance.Reserved : 0));

    private static async Task<IResult> ListProducts(StockFlowDbContext db, AccessService access,
        string? search = null, bool? active = null, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Inventory.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.Products.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Sku.Contains(search) || x.Name.Contains(search));
        }
        if (active.HasValue) query = query.Where(x => x.IsActive == active.Value);
        var total = await query.CountAsync();
        var products = await query.OrderBy(x => x.Sku).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(p => new { p.Id, p.Sku, p.Name, p.ItemType, p.BaseUnit, p.IsActive, p.ReorderLevel }).ToListAsync();
        var productIds = products.Select(p => p.Id).ToArray();
        // Aggregate only this page, avoiding repeated whole-inventory scalar subqueries per product.
        var stock = await db.InventoryBalances.AsNoTracking().Where(b => productIds.Contains(b.StockLot.ProductId))
            .GroupBy(b => b.StockLot.ProductId).Select(g => new
            {
                ProductId = g.Key, OnHand = g.Sum(b => b.OnHand), Reserved = g.Sum(b => b.Reserved),
                Pending = g.Sum(b => b.StockLot.QualityStatus == QualityStatus.Pending ? b.OnHand : 0),
                Rejected = g.Sum(b => b.StockLot.QualityStatus == QualityStatus.Rejected ? b.OnHand : 0),
                Released = g.Sum(b => b.StockLot.QualityStatus == QualityStatus.Released ? b.OnHand : 0),
                Available = g.Sum(b => b.StockLot.QualityStatus == QualityStatus.Released ? b.OnHand - b.Reserved : 0)
            }).ToDictionaryAsync(x => x.ProductId);
        var items = products.Select(p =>
        {
            var balance = stock.GetValueOrDefault(p.Id);
            return new InventoryProductView(p.Id, p.Sku, p.Name, p.ItemType, p.BaseUnit, p.IsActive, p.ReorderLevel,
                balance?.OnHand ?? 0, balance?.Reserved ?? 0, balance?.Pending ?? 0, balance?.Rejected ?? 0,
                balance?.Released ?? 0, balance?.Available ?? 0);
        }).ToArray();
        return Results.Ok(new { items, total, page, pageSize });
    }

    public sealed record LotView(Guid Id, string LotNumber, Guid ProductId, string Sku, string ProductName,
        ItemType ItemType, string BaseUnit, QualityStatus QualityStatus, DateTime ReceivedAtUtc,
        Guid? GoodsReceiptItemId, Guid? ProductionOutputId, decimal OnHand, decimal Reserved, decimal Available);

    public sealed record InventoryProductView(Guid ProductId, string Sku, string Name, ItemType ItemType,
        string BaseUnit, bool IsActive, decimal ReorderLevel, decimal OnHand, decimal Reserved,
        decimal Pending, decimal Rejected, decimal Released, decimal Available)
    {
        public bool LowStock => Available <= ReorderLevel;
    }
}

