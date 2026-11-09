using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Application;

public sealed class PurchasingService(StockFlowDbContext db, AccessService access, CommandExecutor commands)
{
    public async Task<IResult> ListAsync(Guid? departmentId, Guid? supplierId, PurchaseOrderStatus? status,
        string? search, int? page, int? pageSize)
    {
        var currentPage = page ?? 1;
        var currentPageSize = pageSize ?? 20;
        var scope = await access.ScopeAsync("Purchasing.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Permission is required for this scope.");
        Input.ValidatePage(currentPage, currentPageSize);
        var query = db.PurchaseOrders.AsNoTracking()
            .Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId));
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        if (supplierId.HasValue) query = query.Where(x => x.SupplierId == supplierId.Value);
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid purchase order status.");
            query = query.Where(x => x.Status == status.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Number.Contains(search) || x.Supplier.Name.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip((currentPage - 1) * currentPageSize).Take(currentPageSize)
            .Select(x => new PurchaseOrderSummary(x.Id, x.Number, x.DepartmentId, x.SupplierId,
                x.Status, x.CreatedByUserId, x.ApprovedByUserId, x.CreatedAtUtc, x.ApprovedAtUtc, x.Version, x.Items.Count))
            .ToListAsync();
        return Results.Ok(new { items, total, page = currentPage, pageSize = currentPageSize });
    }

    public async Task<IResult> GetAsync(Guid id)
    {
        await RequireOrderPermission(id, "Purchasing.Read");
        return Results.Ok(await View(await FindOrder(id)));
    }

    public Task<IResult> CreateAsync(PurchaseOrderCreate request) =>
        commands.ExecuteAsync(request.RequestId, "PurchaseOrder.Create", request,
            () => access.RequireAsync("Purchasing.Create", request.DepartmentId), async () =>
            {
                await ValidateDraft(request.DepartmentId, request.SupplierId, request.Items);
                var order = new PurchaseOrder
                {
                    Id = Guid.NewGuid(), Number = Input.Code(request.Number, 60), DepartmentId = request.DepartmentId,
                    SupplierId = request.SupplierId, CreatedByUserId = access.UserId, Status = PurchaseOrderStatus.Draft
                };
                ApplyItems(order, request.Items!);
                db.PurchaseOrders.Add(order);
                await db.SaveChangesAsync();
                return await Result(order, "PurchaseOrder.Created", 201);
            });

    public Task<IResult> UpdateAsync(Guid id, PurchaseOrderUpdate request) =>
        commands.ExecuteAsync(request.RequestId, "PurchaseOrder.Update", new { id, request },
            () => RequireOrderPermission(id, "Purchasing.Create"), async () =>
            {
                var order = await FindOrder(id);
                RequireVersion(order, request.Version);
                if (order.Status != PurchaseOrderStatus.Draft)
                    throw new ApiException(409, "Only a Draft purchase order can be edited.");
                await ValidateDraft(order.DepartmentId, request.SupplierId, request.Items);
                order.Number = Input.Code(request.Number, 60);
                order.SupplierId = request.SupplierId;
                ApplyItems(order, request.Items!);
                // Updating only lines must still advance the header's rowversion.
                db.Entry(order).Property(x => x.SupplierId).IsModified = true;
                await db.SaveChangesAsync();
                return await Result(order, "PurchaseOrder.Updated");
            });

    public Task<IResult> ApproveAsync(Guid id, PurchaseOrderTransition request) =>
        commands.ExecuteAsync(request.RequestId, "PurchaseOrder.Approve", new { id, request },
            () => RequireOrderPermission(id, "Purchasing.Approve"), async () =>
            {
                var order = await FindOrder(id);
                RequireVersion(order, request.Version);
                if (order.Status != PurchaseOrderStatus.Draft)
                    throw new ApiException(409, "Only a Draft purchase order can be approved.");
                if (order.CreatedByUserId == access.UserId)
                    throw new ApiException(409, "The purchase order creator cannot approve their own order.");
                await ValidateDraft(order.DepartmentId, order.SupplierId,
                    order.Items.Select(x => new PurchaseOrderLine(x.ProductId, x.Quantity, x.UnitCost)).ToArray());
                order.Status = PurchaseOrderStatus.Approved;
                order.ApprovedByUserId = access.UserId;
                order.ApprovedAtUtc = await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
                await db.SaveChangesAsync();
                return await Result(order, "PurchaseOrder.Approved");
            });

    public Task<IResult> CancelAsync(Guid id, PurchaseOrderTransition request) =>
        commands.ExecuteAsync(request.RequestId, "PurchaseOrder.Cancel", new { id, request },
            () => RequireOrderPermission(id, "Purchasing.Cancel"), async () =>
            {
                var order = await FindOrder(id);
                RequireVersion(order, request.Version);
                if (order.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Approved or PurchaseOrderStatus.PartiallyReceived))
                    throw new ApiException(409, "Only an open purchase order with an unreceived remainder can be cancelled.");
                var received = await ReceivedQuantities(order.Id);
                if (order.Items.All(x => received.GetValueOrDefault(x.Id) >= x.Quantity))
                    throw new ApiException(409, "A fully received purchase order cannot be cancelled.");
                foreach (var item in order.Items)
                    item.CancelledQuantity = item.Quantity - received.GetValueOrDefault(item.Id);
                order.Status = received.Values.Any(x => x > 0) ? PurchaseOrderStatus.Closed : PurchaseOrderStatus.Cancelled;
                await db.SaveChangesAsync();
                return await Result(order, order.Status == PurchaseOrderStatus.Closed ? "PurchaseOrder.Closed" : "PurchaseOrder.Cancelled");
            });

    private async Task RequireOrderPermission(Guid id, string permission)
    {
        var scope = await access.ScopeAsync(permission);
        if (!await db.PurchaseOrders.AnyAsync(x => x.Id == id
            && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private async Task<PurchaseOrder> FindOrder(Guid id) =>
        await db.PurchaseOrders.Include(x => x.Items).ThenInclude(x => x.Product).SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ApiException(404, "Resource not found.");

    private static void RequireVersion(PurchaseOrder order, byte[]? version)
    {
        if (version is not { Length: 8 } || !order.Version.SequenceEqual(version))
            throw new ApiException(409, "The purchase order changed. Reload and supply its current Version.");
    }

    private async Task ValidateDraft(Guid departmentId, Guid supplierId, PurchaseOrderLine[]? items)
    {
        if (!await db.Departments.AnyAsync(x => x.Id == departmentId && x.IsActive))
            throw new ApiException(400, "An active owning department is required.");
        if (!await db.Suppliers.AnyAsync(x => x.Id == supplierId && x.IsActive))
            throw new ApiException(400, "An active supplier is required.");
        if (items is null || items.Length is < 1 or > 20 || items.Any(x => x is null))
            throw new ApiException(400, "A purchase order requires 1–20 lines.");
        if (items.Select(x => x.ProductId).Distinct().Count() != items.Length)
            throw new ApiException(400, "A product can appear only once in a purchase order.");
        foreach (var item in items)
        {
            if (Input.Quantity(item.Quantity) == 0)
                throw new ApiException(400, "Purchase quantities must be positive.");
            Input.Money(item.UnitCost);
        }
        var productIds = items.Select(x => x.ProductId).ToArray();
        if (await db.Products.CountAsync(x => productIds.Contains(x.Id) && x.IsActive && x.ItemType == ItemType.RawMaterial) != items.Length)
            throw new ApiException(400, "Every line must reference an active raw material.");
    }

    private void ApplyItems(PurchaseOrder order, PurchaseOrderLine[] items)
    {
        var products = items.Select(x => x.ProductId).ToHashSet();
        foreach (var removed in order.Items.Where(x => !products.Contains(x.ProductId)).ToArray())
        {
            order.Items.Remove(removed);
            db.PurchaseOrderItems.Remove(removed);
        }
        foreach (var line in items)
        {
            var item = order.Items.SingleOrDefault(x => x.ProductId == line.ProductId);
            if (item is null)
            {
                item = new PurchaseOrderItem { Id = Guid.NewGuid(), PurchaseOrderId = order.Id, ProductId = line.ProductId };
                order.Items.Add(item);
                db.PurchaseOrderItems.Add(item);
            }
            item.Quantity = line.Quantity;
            item.UnitCost = line.UnitCost;
        }
    }

    private async Task<Dictionary<Guid, decimal>> ReceivedQuantities(Guid orderId) =>
        await db.GoodsReceiptItems.Where(x => x.PurchaseOrderItem.PurchaseOrderId == orderId)
            .GroupBy(x => x.PurchaseOrderItemId)
            .Select(x => new { Id = x.Key, Quantity = x.Sum(y => y.ReceivedQuantity) })
            .ToDictionaryAsync(x => x.Id, x => x.Quantity);

    private async Task<PurchaseOrderView> View(PurchaseOrder order)
    {
        var received = await ReceivedQuantities(order.Id);
        var productIds = order.Items.Select(x => x.ProductId).ToArray();
        var products = await db.Products.AsNoTracking().Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        return new PurchaseOrderView(order.Id, order.Number, order.DepartmentId, order.SupplierId, order.Status,
            order.CreatedByUserId, order.ApprovedByUserId, order.CreatedAtUtc, order.ApprovedAtUtc, order.Version,
            order.Items.OrderBy(x => products[x.ProductId].Sku).ThenBy(x => x.Id).Select(x => new PurchaseOrderLineView(
                x.Id, x.ProductId, products[x.ProductId].Sku, products[x.ProductId].Name, products[x.ProductId].BaseUnit,
                x.Quantity, x.UnitCost, received.GetValueOrDefault(x.Id), x.CancelledQuantity,
                x.Quantity - received.GetValueOrDefault(x.Id) - x.CancelledQuantity)).ToArray());
    }

    private async Task<CommandResult<PurchaseOrderView>> Result(PurchaseOrder order, string action, int status = 200)
    {
        var view = await View(order);
        return new CommandResult<PurchaseOrderView>(view, nameof(PurchaseOrder), order.Id, order.DepartmentId, action, view, status);
    }

    public sealed record PurchaseOrderLine(Guid ProductId, decimal Quantity, decimal UnitCost);
    public sealed record PurchaseOrderCreate(Guid RequestId, string Number, Guid DepartmentId, Guid SupplierId, PurchaseOrderLine[]? Items);
    public sealed record PurchaseOrderUpdate(Guid RequestId, string Number, Guid SupplierId, PurchaseOrderLine[]? Items, byte[]? Version);
    public sealed record PurchaseOrderTransition(Guid RequestId, byte[]? Version);
    public sealed record PurchaseOrderSummary(Guid Id, string Number, Guid DepartmentId, Guid SupplierId, PurchaseOrderStatus Status,
        Guid CreatedByUserId, Guid? ApprovedByUserId, DateTime CreatedAtUtc, DateTime? ApprovedAtUtc, byte[] Version, int LineCount);
    public sealed record PurchaseOrderLineView(Guid Id, Guid ProductId, string Sku, string Name, string BaseUnit,
        decimal Quantity, decimal UnitCost, decimal ReceivedQuantity, decimal CancelledQuantity, decimal RemainingQuantity);
    public sealed record PurchaseOrderView(Guid Id, string Number, Guid DepartmentId, Guid SupplierId, PurchaseOrderStatus Status,
        Guid CreatedByUserId, Guid? ApprovedByUserId, DateTime CreatedAtUtc, DateTime? ApprovedAtUtc, byte[] Version, PurchaseOrderLineView[] Items);
}
