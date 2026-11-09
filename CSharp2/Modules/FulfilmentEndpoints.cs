using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class FulfilmentEndpoints
{
    public static IEndpointRouteBuilder MapFulfilmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var orders = endpoints.MapGroup("/api/sales-orders").RequireAuthorization();
        orders.MapPost("/{id:guid}/reserve", Reserve);
        orders.MapPost("/{id:guid}/cancel", Cancel);
        var shipments = endpoints.MapGroup("/api/shipments").RequireAuthorization();
        shipments.MapGet("/", ListShipments);
        shipments.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await RequireShipment(db, access, id, "Shipments.Read");
            return Results.Ok(await View(db, id));
        });
        shipments.MapPost("/", Prepare);
        shipments.MapPost("/{id:guid}/dispatch", Dispatch);
        shipments.MapPost("/{id:guid}/tracking", UpdateTracking);
        shipments.MapPost("/{id:guid}/deliver", Deliver);
        return endpoints;
    }

    private static Task<IResult> Reserve(Guid id, OrderCommand request, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, "SalesOrder.Reserve", new { id, request }, async () =>
        {
            await access.RequireAsync("Inventory.Reserve");
            await RequireOrder(db, access, id, "Orders.Read");
        }, async () =>
        {
            var order = await LockOrder(db, id);
            RequireVersion(order.Version, request.Version, "sales order");
            if (order.Status != SalesOrderStatus.Confirmed || order.Items.Count == 0)
                throw new ApiException(409, "Only a Confirmed sales order can be reserved in full.");
            if (await db.StockReservations.AnyAsync(x => x.SalesOrderItem.SalesOrderId == id))
                throw new ApiException(409, "This order already has stock allocations.");

            var timestamp = await DatabaseTime(db);
            foreach (var item in order.Items.OrderBy(x => x.ProductId))
            {
                var productId = item.ProductId;
                // Lock the candidate set before allocation; competing orders wait for its committed balance.
                var candidates = await db.StockLots.FromSqlInterpolated($"SELECT * FROM [StockLots] WITH (UPDLOCK, HOLDLOCK) WHERE [ProductId] = {productId} AND [QualityStatus] = 'Released'")
                    .OrderBy(x => x.Id).ToListAsync();
                var balances = new Dictionary<Guid, InventoryBalance>();
                foreach (var candidate in candidates)
                    balances[candidate.Id] = await LockBalance(db, candidate.Id);
                var remaining = item.Quantity;
                foreach (var lot in candidates.OrderBy(x => x.ReceivedAtUtc).ThenBy(x => x.Id))
                {
                    var balance = balances[lot.Id];
                    var quantity = Math.Min(remaining, balance.OnHand - balance.Reserved);
                    if (quantity <= 0) continue;
                    var reservation = new StockReservation
                    {
                        SalesOrderItemId = item.Id, StockLotId = lot.Id, Quantity = quantity
                    };
                    db.StockReservations.Add(reservation);
                    balance.Reserved += quantity;
                    db.StockMovements.Add(new StockMovement
                    {
                        StockLotId = lot.Id, StockReservationId = reservation.Id, Type = StockMovementType.Reserve,
                        OnHandDelta = 0, ReservedDelta = quantity, PostedAtUtc = timestamp
                    });
                    remaining -= quantity;
                    if (remaining == 0) break;
                }
                if (remaining > 0)
                    throw new ApiException(409, "Released finished goods are insufficient. No order items were reserved.");
            }
            order.Status = SalesOrderStatus.Reserved;
            await db.SaveChangesAsync();
            var view = await OrderView(db, order);
            return new CommandResult<ReservationView>(view, nameof(SalesOrder), id, order.DepartmentId,
                "Reserved", view);
        });

    private static Task<IResult> Cancel(Guid id, OrderCommand request, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, "SalesOrder.Cancel", new { id, request },
            () => RequireOrder(db, access, id, "Orders.Cancel"), async () =>
            {
                var order = await LockOrder(db, id);
                RequireVersion(order.Version, request.Version, "sales order");
                if (order.Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Confirmed or SalesOrderStatus.Reserved))
                    throw new ApiException(409, "Only an undispatched order can be cancelled.");
                var workIds = await db.ProductionOrders.Where(x => x.SalesOrderItem.SalesOrderId == id)
                    .OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
                foreach (var workId in workIds)
                {
                    var work = await db.ProductionOrders.FromSqlInterpolated($"SELECT * FROM [ProductionOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {workId}").SingleAsync();
                    if (work.Status is not (ProductionOrderStatus.Draft or ProductionOrderStatus.Released or ProductionOrderStatus.Cancelled)
                        || await db.MaterialIssues.AnyAsync(x => x.ProductionOrderId == workId))
                        throw new ApiException(409, "An order cannot be cancelled after linked material issue.");
                    work.Status = ProductionOrderStatus.Cancelled;
                }
                var shipment = await db.Shipments.Where(x => x.SalesOrderId == id).SingleOrDefaultAsync();
                if (shipment is not null)
                {
                    if (shipment.Status != ShipmentStatus.Prepared)
                        throw new ApiException(409, "A dispatched shipment cannot be cancelled.");
                    shipment.Status = ShipmentStatus.Cancelled;
                }
                var reservations = await db.StockReservations.Where(x => x.SalesOrderItem.SalesOrderId == id
                    && x.Status == ReservationStatus.Active).OrderBy(x => x.StockLotId).ThenBy(x => x.Id).ToListAsync();
                var timestamp = await DatabaseTime(db);
                foreach (var reservation in reservations)
                {
                    var balance = await LockBalance(db, reservation.StockLotId);
                    if (balance.Reserved < reservation.Quantity)
                        throw new ApiException(409, "The reservation balance is inconsistent; cancellation was not posted.");
                    balance.Reserved -= reservation.Quantity;
                    reservation.Status = ReservationStatus.Released;
                    db.StockMovements.Add(new StockMovement
                    {
                        StockLotId = reservation.StockLotId, StockReservationId = reservation.Id,
                        Type = StockMovementType.Release, OnHandDelta = 0,
                        ReservedDelta = -reservation.Quantity, PostedAtUtc = timestamp
                    });
                }
                order.Status = SalesOrderStatus.Cancelled;
                await db.SaveChangesAsync();
                var view = await OrderView(db, order);
                return new CommandResult<ReservationView>(view, nameof(SalesOrder), id, order.DepartmentId,
                    "Cancelled", new { order = view, cancelledProductionOrders = workIds, cancelledShipmentId = shipment?.Id });
            });

    private static Task<IResult> Prepare(ShipmentPrepare request, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, "Shipment.Prepare", request, async () =>
        {
            await access.RequireAsync("Shipments.Prepare", request.DepartmentId);
            await RequireOrder(db, access, request.SalesOrderId, "Orders.Read");
        }, async () =>
        {
            var order = await LockOrder(db, request.SalesOrderId);
            RequireVersion(order.Version, request.OrderVersion, "sales order");
            if (order.Status != SalesOrderStatus.Reserved)
                throw new ApiException(409, "A shipment requires a fully Reserved order.");
            if (!await db.Departments.AnyAsync(x => x.Id == request.DepartmentId && x.IsActive))
                throw new ApiException(400, "An active shipment department is required.");
            if (await db.Shipments.AnyAsync(x => x.SalesOrderId == order.Id))
                throw new ApiException(409, "A shipment already exists for this order.");
            var reservations = await db.StockReservations.Where(x => x.SalesOrderItem.SalesOrderId == order.Id
                && x.Status == ReservationStatus.Active).ToListAsync();
            RequireFullReservations(order, reservations);
            var address = await db.Customers.Where(x => x.Id == order.CustomerId).Select(x => x.DeliveryAddress).SingleAsync();
            var shipment = new Shipment
            {
                Number = Input.Code(request.Number, 60), DepartmentId = request.DepartmentId,
                SalesOrderId = order.Id, CarrierName = Input.Required(request.CarrierName, 200),
                TrackingNumber = Input.Required(request.TrackingNumber, 200),
                DeliveryAddress = Input.Required(address, 1000)
            };
            foreach (var reservation in reservations)
                shipment.Items.Add(new ShipmentItem { ShipmentId = shipment.Id, StockReservationId = reservation.Id });
            db.Shipments.Add(shipment);
            await db.SaveChangesAsync();
            return await ShipmentResult(db, shipment, "Prepared", 201);
        });

    private static Task<IResult> Dispatch(Guid id, ShipmentCommand request, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, "Shipment.Dispatch", new { id, request }, async () =>
        {
            await access.RequireAsync("Inventory.Dispatch");
            await RequireShipment(db, access, id, "Shipments.Read");
        }, async () =>
        {
            var (order, shipment) = await LockShipment(db, id);
            RequireVersion(shipment.Version, request.Version, "shipment");
            if (shipment.Status != ShipmentStatus.Prepared || order.Status != SalesOrderStatus.Reserved)
                throw new ApiException(409, "Only a Prepared shipment for a Reserved order can be dispatched.");
            var reservations = await db.StockReservations.Where(x => x.SalesOrderItem.SalesOrderId == order.Id
                && x.Status == ReservationStatus.Active).ToListAsync();
            RequireFullReservations(order, reservations);
            if (shipment.Items.Count != reservations.Count || shipment.Items.Select(x => x.StockReservationId)
                .Except(reservations.Select(x => x.Id)).Any())
                throw new ApiException(409, "The shipment must contain every active order reservation once.");
            var timestamp = await DatabaseTime(db);
            foreach (var group in reservations.GroupBy(x => x.StockLotId).OrderBy(x => x.Key))
            {
                var lotId = group.Key;
                var lot = await db.StockLots.FromSqlInterpolated($"SELECT * FROM [StockLots] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {lotId}").SingleAsync();
                if (lot.QualityStatus != QualityStatus.Released || group.Any(x => order.Items.Single(y => y.Id == x.SalesOrderItemId).ProductId != lot.ProductId))
                    throw new ApiException(409, "Shipment allocations must reference released matching finished lots.");
                var balance = await LockBalance(db, lotId);
                var quantity = group.Sum(x => x.Quantity);
                if (balance.OnHand < quantity || balance.Reserved < quantity)
                    throw new ApiException(409, "Stock no longer covers this shipment. Dispatch was not posted.");
                balance.OnHand -= quantity;
                balance.Reserved -= quantity;
                foreach (var reservation in group)
                {
                    reservation.Status = ReservationStatus.Consumed;
                    var item = shipment.Items.Single(x => x.StockReservationId == reservation.Id);
                    db.StockMovements.Add(new StockMovement
                    {
                        StockLotId = lotId, ShipmentItemId = item.Id, Type = StockMovementType.Dispatch,
                        OnHandDelta = -reservation.Quantity, ReservedDelta = -reservation.Quantity, PostedAtUtc = timestamp
                    });
                }
            }
            shipment.Status = ShipmentStatus.Dispatched;
            shipment.DispatchedAtUtc = timestamp;
            order.Status = SalesOrderStatus.Dispatched;
            await db.SaveChangesAsync();
            return await ShipmentResult(db, shipment, "Dispatched");
        });

    private static Task<IResult> UpdateTracking(Guid id, ShipmentTracking request, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, "Shipment.UpdateTracking", new { id, request },
            () => RequireShipment(db, access, id, "Shipments.Deliver"), async () =>
            {
                var (_, shipment) = await LockShipment(db, id);
                RequireVersion(shipment.Version, request.Version, "shipment");
                if (shipment.Status is not (ShipmentStatus.Prepared or ShipmentStatus.Dispatched))
                    throw new ApiException(409, "Tracking notes can change only for a Prepared or Dispatched shipment.");
                var previousNotes = shipment.TrackingNotes;
                shipment.TrackingNotes = Input.Required(request.Notes, 2000);
                await db.SaveChangesAsync();
                var view = await View(db, shipment.Id);
                return new CommandResult<ShipmentView>(view, nameof(Shipment), shipment.Id, shipment.DepartmentId,
                    "TrackingUpdated", new { previousNotes, notes = shipment.TrackingNotes });
            });

    private static Task<IResult> Deliver(Guid id, ShipmentDelivery request, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, "Shipment.Deliver", new { id, request },
            () => RequireShipment(db, access, id, "Shipments.Deliver"), async () =>
            {
                var (order, shipment) = await LockShipment(db, id);
                RequireVersion(shipment.Version, request.Version, "shipment");
                if (shipment.Status != ShipmentStatus.Dispatched || order.Status != SalesOrderStatus.Dispatched)
                    throw new ApiException(409, "Only a Dispatched shipment can be delivered.");
                var timestamp = await DatabaseTime(db);
                if (request.DeliveredAtUtc == default || request.DeliveredAtUtc.Kind != DateTimeKind.Utc
                    || request.DeliveredAtUtc < shipment.DispatchedAtUtc || request.DeliveredAtUtc > timestamp)
                    throw new ApiException(400, "DeliveredAtUtc must be UTC, on or after dispatch, and no later than now.");
                shipment.DeliveredAtUtc = request.DeliveredAtUtc;
                shipment.Status = ShipmentStatus.Delivered;
                order.Status = SalesOrderStatus.Completed;
                await db.SaveChangesAsync();
                return await ShipmentResult(db, shipment, "Delivered");
            });

    private static async Task<IResult> ListShipments(StockFlowDbContext db, AccessService access,
        Guid? departmentId = null, Guid? salesOrderId = null, ShipmentStatus? status = null,
        string? search = null, int page = 1, int pageSize = 20)
    {
        var scope = await access.ScopeAsync("Shipments.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Permission is required for this scope.");
        var orders = await access.ScopeAsync("Orders.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.Shipments.AsNoTracking().Where(x => (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))
            && (orders.CompanyWide || orders.DepartmentIds.Contains(x.SalesOrder.DepartmentId)));
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
        if (salesOrderId.HasValue) query = query.Where(x => x.SalesOrderId == salesOrderId.Value);
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid shipment status.");
            query = query.Where(x => x.Status == status.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Number.Contains(search) || x.TrackingNumber.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await query.OrderBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new { x.Id, x.Number, x.DepartmentId, x.SalesOrderId, x.Status, x.CarrierName,
                x.TrackingNumber, DispatchedAtUtc = Utc(x.DispatchedAtUtc),
                DeliveredAtUtc = Utc(x.DeliveredAtUtc), x.Version }).ToListAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task RequireOrder(StockFlowDbContext db, AccessService access, Guid id, string permission)
    {
        var scope = await access.ScopeAsync(permission);
        if (!await db.SalesOrders.AnyAsync(x => x.Id == id && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private static async Task RequireShipment(StockFlowDbContext db, AccessService access, Guid id, string permission)
    {
        var scope = await access.ScopeAsync(permission);
        var orders = await access.ScopeAsync("Orders.Read");
        if (!await db.Shipments.AnyAsync(x => x.Id == id && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))
            && (orders.CompanyWide || orders.DepartmentIds.Contains(x.SalesOrder.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private static async Task<SalesOrder> LockOrder(StockFlowDbContext db, Guid id) =>
        await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM [SalesOrders] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .Include(x => x.Items).SingleOrDefaultAsync() ?? throw new ApiException(404, "Resource not found.");

    private static async Task<(SalesOrder, Shipment)> LockShipment(StockFlowDbContext db, Guid id)
    {
        var orderId = await db.Shipments.Where(x => x.Id == id).Select(x => (Guid?)x.SalesOrderId).SingleOrDefaultAsync()
            ?? throw new ApiException(404, "Resource not found.");
        var order = await LockOrder(db, orderId);
        var shipment = await db.Shipments.FromSqlInterpolated($"SELECT * FROM [Shipments] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}")
            .Include(x => x.Items).SingleAsync();
        return (order, shipment);
    }

    private static async Task<InventoryBalance> LockBalance(StockFlowDbContext db, Guid lotId) =>
        await db.InventoryBalances.FromSqlInterpolated($"SELECT * FROM [InventoryBalances] WITH (UPDLOCK, HOLDLOCK) WHERE [StockLotId] = {lotId}")
            .SingleOrDefaultAsync() ?? throw new ApiException(409, "The lot has no inventory balance.");

    private static void RequireVersion(byte[] actual, byte[]? version, string document)
    {
        if (version is not { Length: 8 } || !actual.SequenceEqual(version))
            throw new ApiException(409, $"The {document} changed. Reload and supply its current Version.");
    }

    private static void RequireFullReservations(SalesOrder order, List<StockReservation> reservations)
    {
        if (order.Items.Count == 0 || reservations.Count == 0 || reservations.Any(x => order.Items.All(y => y.Id != x.SalesOrderItemId))
            || order.Items.Any(x => reservations.Where(y => y.SalesOrderItemId == x.Id).Sum(y => y.Quantity) != x.Quantity))
            throw new ApiException(409, "Every order item must be covered in full by active reservations.");
    }

    private static async Task<ReservationView> OrderView(StockFlowDbContext db, SalesOrder order) =>
        new(order.Id, order.Status, order.Version, await db.StockReservations.AsNoTracking()
            .Where(x => x.SalesOrderItem.SalesOrderId == order.Id).OrderBy(x => x.Id)
            .Select(x => new ReservationLine(x.Id, x.SalesOrderItemId, x.StockLotId, x.Quantity, x.Status)).ToListAsync());

    private static async Task<ShipmentView> View(StockFlowDbContext db, Guid id)
    {
        var shipment = await db.Shipments.AsNoTracking().Include(x => x.Items).ThenInclude(x => x.StockReservation)
            .SingleAsync(x => x.Id == id);
        return new ShipmentView(shipment.Id, shipment.Number, shipment.DepartmentId, shipment.SalesOrderId,
            shipment.Status, shipment.CarrierName, shipment.TrackingNumber, shipment.DeliveryAddress,
            shipment.TrackingNotes, Utc(shipment.DispatchedAtUtc), Utc(shipment.DeliveredAtUtc), shipment.Version,
            shipment.Items.OrderBy(x => x.Id).Select(x => new ShipmentLine(x.Id, x.StockReservationId,
                x.StockReservation.SalesOrderItemId, x.StockReservation.StockLotId, x.StockReservation.Quantity)).ToArray());
    }

    private static async Task<CommandResult<ShipmentView>> ShipmentResult(StockFlowDbContext db, Shipment shipment, string action, int status = 200)
    {
        var view = await View(db, shipment.Id);
        return new CommandResult<ShipmentView>(view, nameof(Shipment), shipment.Id, shipment.DepartmentId, action, view, status);
    }

    private static DateTime? Utc(DateTime? date) => date.HasValue ? DateTime.SpecifyKind(date.Value, DateTimeKind.Utc) : null;
    private static async Task<DateTime> DatabaseTime(StockFlowDbContext db) =>
        DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(), DateTimeKind.Utc);

    public sealed record OrderCommand(Guid RequestId, byte[]? Version);
    public sealed record ShipmentPrepare(Guid RequestId, string Number, Guid DepartmentId, Guid SalesOrderId,
        byte[]? OrderVersion, string CarrierName, string TrackingNumber);
    public sealed record ShipmentCommand(Guid RequestId, byte[]? Version);
    public sealed record ShipmentTracking(Guid RequestId, byte[]? Version, string Notes);
    public sealed record ShipmentDelivery(Guid RequestId, byte[]? Version, DateTime DeliveredAtUtc);
    public sealed record ReservationLine(Guid Id, Guid SalesOrderItemId, Guid StockLotId, decimal Quantity, ReservationStatus Status);
    public sealed record ReservationView(Guid Id, SalesOrderStatus Status, byte[] Version, List<ReservationLine> Reservations);
    public sealed record ShipmentLine(Guid Id, Guid StockReservationId, Guid SalesOrderItemId, Guid StockLotId, decimal Quantity);
    public sealed record ShipmentView(Guid Id, string Number, Guid DepartmentId, Guid SalesOrderId, ShipmentStatus Status,
        string CarrierName, string TrackingNumber, string DeliveryAddress, string TrackingNotes, DateTime? DispatchedAtUtc,
        DateTime? DeliveredAtUtc, byte[] Version, ShipmentLine[] Items);
}
