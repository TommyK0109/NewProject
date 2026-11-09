using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class InventoryTraceEndpoints
{
    public static IEndpointRouteBuilder MapInventoryTraceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventory/lots/{id:guid}/trace", TraceLot).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> TraceLot(Guid id, StockFlowDbContext db, AccessService access,
        int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Inventory.Read", conceal: true);
        var scope = await access.ScopeAsync("Production.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(404, "Resource not found.");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var lot = await db.StockLots.AsNoTracking().Where(x => x.Id == id
                && (x.ProductionOutputId == null || scope.CompanyWide
                    || scope.DepartmentIds.Contains(x.ProductionOutput!.ProductionOrder.DepartmentId)))
            .Select(x => new
            {
                x.Id, x.LotNumber, x.ProductId, x.Product.Sku, ProductName = x.Product.Name, x.Product.BaseUnit,
                x.QualityStatus, ReceivedAtUtc = DateTime.SpecifyKind(x.ReceivedAtUtc, DateTimeKind.Utc), x.Balance.OnHand, x.Balance.Reserved,
                Available = x.QualityStatus == QualityStatus.Released ? x.Balance.OnHand - x.Balance.Reserved : 0,
                x.GoodsReceiptItemId, GoodsReceiptId = (Guid?)x.GoodsReceiptItem!.GoodsReceiptId,
                PurchaseOrderId = (Guid?)x.GoodsReceiptItem!.GoodsReceipt.PurchaseOrderId,
                x.ProductionOutputId, ProductionOrderId = (Guid?)x.ProductionOutput!.ProductionOrderId
            }).SingleOrDefaultAsync() ?? throw new ApiException(404, "Resource not found.");

        var query = db.MaterialIssueItems.AsNoTracking().Where(x =>
            scope.CompanyWide || scope.DepartmentIds.Contains(x.MaterialIssue.ProductionOrder.DepartmentId));
        query = lot.ProductionOrderId.HasValue
            ? query.Where(x => x.MaterialIssue.ProductionOrderId == lot.ProductionOrderId.Value)
            : query.Where(x => x.StockLotId == id);
        var total = await query.CountAsync();
        var allocations = query.OrderBy(x => x.MaterialIssue.PostedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize);
        var items = await (from allocation in allocations
            join output in db.ProductionOutputs on allocation.MaterialIssue.ProductionOrderId equals output.ProductionOrderId into outputs
            from output in outputs.DefaultIfEmpty()
            join finished in db.StockLots on output.Id equals finished.ProductionOutputId into finishedLots
            from finished in finishedLots.DefaultIfEmpty()
            orderby allocation.MaterialIssue.PostedAtUtc, allocation.Id
            select new TraceRow(allocation.Id, allocation.Quantity, allocation.StockLotId,
                allocation.StockLot.LotNumber, allocation.StockLot.ProductId, allocation.StockLot.Product.Sku,
                allocation.StockLot.Product.BaseUnit, allocation.MaterialIssueId, allocation.ProductionRequirementId,
                DateTime.SpecifyKind(allocation.MaterialIssue.PostedAtUtc, DateTimeKind.Utc), allocation.MaterialIssue.ProductionOrderId,
                allocation.MaterialIssue.ProductionOrder.Number, allocation.MaterialIssue.ProductionOrder.Status,
                allocation.MaterialIssue.ProductionOrder.DepartmentId,
                output == null ? null : output.Id, output == null ? null : output.ProducedQuantity,
                output == null ? null : output.ScrapQuantity, finished == null ? null : finished.Id,
                finished == null ? null : finished.LotNumber, finished == null ? null : finished.QualityStatus,
                finished == null ? null : finished.ProductId, finished == null ? null : finished.Product.Sku))
            .ToListAsync();
        var shippingScope = await access.ScopeAsync("Shipments.Read");
        var orderScope = await access.ScopeAsync("Orders.Read");
        var downstreamWork = db.MaterialIssueItems.Where(x => x.StockLotId == id
            && (scope.CompanyWide || scope.DepartmentIds.Contains(x.MaterialIssue.ProductionOrder.DepartmentId)))
            .Select(x => x.MaterialIssue.ProductionOrderId);
        var shipments = db.ShipmentItems.AsNoTracking().Where(x =>
            (shippingScope.CompanyWide || shippingScope.DepartmentIds.Contains(x.Shipment.DepartmentId))
            && (orderScope.CompanyWide || orderScope.DepartmentIds.Contains(x.Shipment.SalesOrder.DepartmentId))
            && (lot.ProductionOrderId.HasValue ? x.StockReservation.StockLotId == id
                : x.StockReservation.StockLot.ProductionOutputId != null
                    && downstreamWork.Contains(x.StockReservation.StockLot.ProductionOutput!.ProductionOrderId)));
        var shipmentTotal = await shipments.CountAsync();
        var shipmentItems = await shipments.OrderBy(x => x.Shipment.Id).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).Select(x => new
            {
                ShipmentItemId = x.Id, x.ShipmentId, ShipmentNumber = x.Shipment.Number,
                ShipmentStatus = x.Shipment.Status, x.Shipment.SalesOrderId,
                SalesOrderNumber = x.Shipment.SalesOrder.Number,
                x.Shipment.SalesOrder.CustomerId, CustomerName = x.Shipment.SalesOrder.Customer.Name,
                x.StockReservation.StockLotId, x.StockReservation.StockLot.LotNumber,
                x.StockReservation.Quantity, ReservationStatus = x.StockReservation.Status,
                DispatchedAtUtc = x.Shipment.DispatchedAtUtc.HasValue
                    ? DateTime.SpecifyKind(x.Shipment.DispatchedAtUtc.Value, DateTimeKind.Utc) : (DateTime?)null,
                DeliveredAtUtc = x.Shipment.DeliveredAtUtc.HasValue
                    ? DateTime.SpecifyKind(x.Shipment.DeliveredAtUtc.Value, DateTimeKind.Utc) : (DateTime?)null
            }).ToListAsync();
        return Results.Ok(new { lot, items, total, shipments = shipmentItems, shipmentTotal,
            page, pageSize, traceEndsAt = "CustomerShipment" });
    }

    public sealed record TraceRow(Guid MaterialIssueItemId, decimal Quantity, Guid RawStockLotId,
        string RawLotNumber, Guid RawProductId, string RawSku, string RawBaseUnit, Guid MaterialIssueId,
        Guid ProductionRequirementId, DateTime IssuedAtUtc, Guid ProductionOrderId, string ProductionOrderNumber,
        ProductionOrderStatus ProductionStatus, Guid ProductionDepartmentId, Guid? ProductionOutputId,
        decimal? ProducedQuantity, decimal? ScrapQuantity, Guid? FinishedStockLotId, string? FinishedLotNumber,
        QualityStatus? FinishedQualityStatus, Guid? FinishedProductId, string? FinishedSku);
}
