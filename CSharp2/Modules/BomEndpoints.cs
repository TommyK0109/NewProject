using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class BomEndpoints
{
    public static IEndpointRouteBuilder MapBomEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var boms = endpoints.MapGroup("/api/boms").RequireAuthorization();
        boms.MapGet("/", ListBoms);
        boms.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Bom.Read", conceal: true);
            return Results.Ok(await View(db, await FindBom(db, id)));
        });
        boms.MapPost("/", (BomCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Bom.Create", request,
                () => access.RequireAsync("Bom.Manage"), async () =>
                {
                    if (request.Revision <= 0) throw new ApiException(400, "Revision must be a positive integer.");
                    await ValidateItems(db, request.FinishedProductId, request.Items);
                    var bom = new BillOfMaterial
                    {
                        Id = Guid.NewGuid(), FinishedProductId = request.FinishedProductId,
                        Revision = request.Revision, Status = BomStatus.Draft
                    };
                    ApplyItems(db, bom, request.Items!);
                    db.BillOfMaterials.Add(bom);
                    await db.SaveChangesAsync();
                    return await Result(db, bom, "Bom.Created", 201);
                }));
        boms.MapPut("/{id:guid}", (Guid id, BomUpdate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Bom.Update", new { id, request },
                () => access.RequireAsync("Bom.Manage", conceal: true), async () =>
                {
                    var bom = await FindBom(db, id);
                    RequireVersion(bom, request.Version);
                    if (bom.Status != BomStatus.Draft)
                        throw new ApiException(409, "An approved BOM revision is immutable. Create a new revision instead.");
                    await ValidateItems(db, bom.FinishedProductId, request.Items);
                    ApplyItems(db, bom, request.Items!);
                    // A line-only edit must also advance the revision's rowversion.
                    db.Entry(bom).Property(x => x.Status).IsModified = true;
                    await db.SaveChangesAsync();
                    return await Result(db, bom, "Bom.Updated");
                }));
        boms.MapPost("/{id:guid}/approve", (Guid id, BomApprove request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Bom.Approve", new { id, request },
                () => access.RequireAsync("Bom.Approve", conceal: true), async () =>
                {
                    var bom = await FindBom(db, id);
                    RequireVersion(bom, request.Version);
                    if (bom.Status != BomStatus.Draft)
                        throw new ApiException(409, "Only a Draft BOM revision can be approved.");
                    await ValidateItems(db, bom.FinishedProductId,
                        bom.Items.Select(x => new BomLine(x.MaterialProductId, x.QuantityPerUnit)).ToArray());
                    bom.Status = BomStatus.Approved;
                    bom.ApprovedByUserId = access.UserId;
                    bom.ApprovedAtUtc = await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
                    await db.SaveChangesAsync();
                    return await Result(db, bom, "Bom.Approved");
                }));
        return endpoints;
    }

    private static async Task<IResult> ListBoms(StockFlowDbContext db, AccessService access,
        Guid? finishedProductId = null, BomStatus? status = null, string? search = null,
        int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Bom.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.BillOfMaterials.AsNoTracking();
        if (finishedProductId.HasValue) query = query.Where(x => x.FinishedProductId == finishedProductId.Value);
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid BOM status.");
            query = query.Where(x => x.Status == status.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.FinishedProduct.Sku.Contains(search) || x.FinishedProduct.Name.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.FinishedProduct.Sku).ThenByDescending(x => x.Revision).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new BomSummary(x.Id, x.FinishedProductId, x.FinishedProduct.Sku, x.Revision, x.Status,
                x.ApprovedByUserId, x.ApprovedAtUtc, x.Version, x.Items.Count)).ToListAsync();
        return Results.Ok(new
        {
            items = items.Select(x => x with { ApprovedAtUtc = x.ApprovedAtUtc.HasValue
                ? DateTime.SpecifyKind(x.ApprovedAtUtc.Value, DateTimeKind.Utc) : null }),
            total, page, pageSize
        });
    }

    private static async Task<BillOfMaterial> FindBom(StockFlowDbContext db, Guid id) =>
        await db.BillOfMaterials.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ApiException(404, "Resource not found.");

    private static void RequireVersion(BillOfMaterial bom, byte[]? version)
    {
        if (version is not { Length: 8 } || !bom.Version.SequenceEqual(version))
            throw new ApiException(409, "The BOM revision changed. Reload and supply its current Version.");
    }

    private static async Task ValidateItems(StockFlowDbContext db, Guid finishedProductId, BomLine[]? items)
    {
        if (!await db.Products.AnyAsync(x => x.Id == finishedProductId && x.IsActive && x.ItemType == ItemType.FinishedGood))
            throw new ApiException(400, "An active finished good is required.");
        if (items is null || items.Length is < 1 or > 20 || items.Any(x => x is null))
            throw new ApiException(400, "A BOM requires 1–20 material lines.");
        if (items.Select(x => x.MaterialProductId).Distinct().Count() != items.Length)
            throw new ApiException(400, "A material can appear only once in a BOM revision.");
        foreach (var item in items)
            if (Input.Quantity(item.QuantityPerUnit) == 0)
                throw new ApiException(400, "BOM quantities per unit must be positive.");
        var materialIds = items.Select(x => x.MaterialProductId).ToArray();
        if (await db.Products.CountAsync(x => materialIds.Contains(x.Id) && x.IsActive && x.ItemType == ItemType.RawMaterial) != items.Length)
            throw new ApiException(400, "Every BOM material must reference an active raw material.");
    }

    private static void ApplyItems(StockFlowDbContext db, BillOfMaterial bom, BomLine[] items)
    {
        var materialIds = items.Select(x => x.MaterialProductId).ToHashSet();
        foreach (var removed in bom.Items.Where(x => !materialIds.Contains(x.MaterialProductId)).ToArray())
        {
            bom.Items.Remove(removed);
            db.BomItems.Remove(removed);
        }
        foreach (var line in items)
        {
            var item = bom.Items.SingleOrDefault(x => x.MaterialProductId == line.MaterialProductId);
            if (item is null)
            {
                item = new BomItem { Id = Guid.NewGuid(), BillOfMaterialId = bom.Id, MaterialProductId = line.MaterialProductId };
                bom.Items.Add(item);
                db.BomItems.Add(item);
            }
            item.QuantityPerUnit = line.QuantityPerUnit;
        }
    }

    private static async Task<BomView> View(StockFlowDbContext db, BillOfMaterial bom)
    {
        var productIds = bom.Items.Select(x => x.MaterialProductId).Append(bom.FinishedProductId).ToArray();
        var products = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var finished = products[bom.FinishedProductId];
        return new BomView(bom.Id, bom.FinishedProductId, finished.Sku, finished.Name, finished.BaseUnit,
            bom.Revision, bom.Status, bom.ApprovedByUserId,
            bom.ApprovedAtUtc.HasValue ? DateTime.SpecifyKind(bom.ApprovedAtUtc.Value, DateTimeKind.Utc) : null, bom.Version,
            bom.Items.OrderBy(x => products[x.MaterialProductId].Sku).ThenBy(x => x.Id).Select(x => new BomLineView(
                x.Id, x.MaterialProductId, products[x.MaterialProductId].Sku, products[x.MaterialProductId].Name,
                products[x.MaterialProductId].BaseUnit, x.QuantityPerUnit)).ToArray());
    }

    private static async Task<CommandResult<BomView>> Result(StockFlowDbContext db, BillOfMaterial bom, string action, int status = 200)
    {
        var view = await View(db, bom);
        return new CommandResult<BomView>(view, nameof(BillOfMaterial), bom.Id, null, action, view, status);
    }

    public sealed record BomLine(Guid MaterialProductId, decimal QuantityPerUnit);
    public sealed record BomCreate(Guid RequestId, Guid FinishedProductId, int Revision, BomLine[]? Items);
    public sealed record BomUpdate(Guid RequestId, BomLine[]? Items, byte[]? Version);
    public sealed record BomApprove(Guid RequestId, byte[]? Version);
    public sealed record BomSummary(Guid Id, Guid FinishedProductId, string Sku, int Revision, BomStatus Status,
        Guid? ApprovedByUserId, DateTime? ApprovedAtUtc, byte[] Version, int LineCount);
    public sealed record BomLineView(Guid Id, Guid MaterialProductId, string Sku, string Name, string BaseUnit, decimal QuantityPerUnit);
    public sealed record BomView(Guid Id, Guid FinishedProductId, string Sku, string Name, string BaseUnit, int Revision,
        BomStatus Status, Guid? ApprovedByUserId, DateTime? ApprovedAtUtc, byte[] Version, BomLineView[] Items);
}
