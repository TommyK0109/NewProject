using System.Diagnostics.Metrics;
using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class ReportEndpoints
{
    private static readonly Meter Meter = new("StockFlow.Inventory");
    private static readonly Counter<long> ReconciliationFailures = Meter.CreateCounter<long>("stockflow.inventory.reconciliation_failures");

    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var reports = endpoints.MapGroup("/api/reports").RequireAuthorization();
        reports.MapGet("/dashboard", Dashboard);
        reports.MapGet("/reconciliation", Reconciliation);
        return endpoints;
    }

    private static async Task<IResult> Dashboard(StockFlowDbContext db, AccessService access, Guid? departmentId = null)
    {
        var result = new Dictionary<string, object>();
        var orders = await access.ScopeAsync("Orders.Read");
        if (CanRead(orders))
        {
            var query = db.SalesOrders.AsNoTracking().Where(x => orders.CompanyWide || orders.DepartmentIds.Contains(x.DepartmentId));
            if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
            var lines = db.SalesOrderItems.Where(x => query.Any(o => o.Id == x.SalesOrderId));
            result["sales"] = new
            {
                orders = await query.CountAsync(),
                statuses = await query.GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(),
                openDemandQuantity = await lines.Where(x => x.SalesOrder.Status == SalesOrderStatus.Confirmed)
                    .SumAsync(x => (decimal?)x.Quantity) ?? 0,
                completedOrderValue = await lines.Where(x => x.SalesOrder.Status == SalesOrderStatus.Completed)
                    .SumAsync(x => (decimal?)Math.Round(x.Quantity * x.UnitPrice, 2)) ?? 0
            };
        }
        var production = await access.ScopeAsync("Production.Read");
        if (CanRead(production))
        {
            var query = db.ProductionOrders.AsNoTracking().Where(x => production.CompanyWide || production.DepartmentIds.Contains(x.DepartmentId));
            if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
            var outputs = db.ProductionOutputs.Where(x => query.Any(o => o.Id == x.ProductionOrderId));
            result["production"] = new
            {
                orders = await query.CountAsync(),
                statuses = await query.GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync(),
                producedQuantity = await outputs.SumAsync(x => (decimal?)x.ProducedQuantity) ?? 0,
                scrapQuantity = await outputs.SumAsync(x => (decimal?)x.ScrapQuantity) ?? 0,
                rejectedOutputQuantity = await db.StockLots.Where(x => x.QualityStatus == QualityStatus.Rejected
                    && x.ProductionOutputId.HasValue && outputs.Any(o => o.Id == x.ProductionOutputId))
                    .SumAsync(x => (decimal?)x.Balance.OnHand) ?? 0
            };
        }
        var quality = await access.ScopeAsync("Quality.Read");
        if (quality.CompanyWide)
            result["quality"] = new
            {
                pendingInspections = await db.QualityInspections.CountAsync(x => x.Status == QualityInspectionStatus.Pending),
                pendingQuantity = await db.InventoryBalances.Where(x => x.StockLot.QualityStatus == QualityStatus.Pending).SumAsync(x => (decimal?)x.OnHand) ?? 0,
                rejectedQuantity = await db.InventoryBalances.Where(x => x.StockLot.QualityStatus == QualityStatus.Rejected).SumAsync(x => (decimal?)x.OnHand) ?? 0
            };
        var inventory = await access.ScopeAsync("Inventory.Read");
        if (inventory.CompanyWide)
        {
            var balances = db.InventoryBalances.AsNoTracking();
            result["inventory"] = new
            {
                lots = await balances.CountAsync(),
                onHand = await balances.SumAsync(x => (decimal?)x.OnHand) ?? 0,
                reserved = await balances.SumAsync(x => (decimal?)x.Reserved) ?? 0,
                available = await balances.Where(x => x.StockLot.QualityStatus == QualityStatus.Released)
                    .SumAsync(x => (decimal?)(x.OnHand - x.Reserved)) ?? 0,
                lowStockProducts = await db.Products.CountAsync(p => p.IsActive
                    && (db.InventoryBalances.Where(b => b.StockLot.ProductId == p.Id && b.StockLot.QualityStatus == QualityStatus.Released)
                        .Sum(b => (decimal?)(b.OnHand - b.Reserved)) ?? 0) <= p.ReorderLevel)
            };
        }
        var shipping = await access.ScopeAsync("Shipments.Read");
        if (CanRead(shipping) && CanRead(orders))
        {
            var query = db.Shipments.AsNoTracking()
                .Where(x => shipping.CompanyWide || shipping.DepartmentIds.Contains(x.DepartmentId))
                .Where(x => orders.CompanyWide || orders.DepartmentIds.Contains(x.SalesOrder.DepartmentId));
            if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
            result["shipments"] = await query.GroupBy(x => x.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync();
        }
        var employees = await access.ScopeAsync("Employees.Read");
        if (CanRead(employees))
        {
            var query = db.Employees.AsNoTracking().Where(x => employees.CompanyWide || employees.DepartmentIds.Contains(x.DepartmentId));
            if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
            result["employees"] = await query.GroupBy(x => x.EmploymentStatus)
                .Select(g => new { status = g.Key, count = g.Count() }).ToListAsync();
        }
        var finance = await access.ScopeAsync("Finance.Read");
        if (CanRead(finance))
        {
            var query = db.Invoices.AsNoTracking().Where(x => finance.CompanyWide || finance.DepartmentIds.Contains(x.DepartmentId));
            if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId.Value);
            var totals = new List<object>();
            foreach (var type in Enum.GetValues<InvoiceType>())
            {
                var invoices = query.Where(x => x.Type == type);
                var invoicedValue = await invoices.Where(x => x.Status != InvoiceStatus.Draft).SumAsync(x => (decimal?)x.Total) ?? 0;
                var recordedPayments = await db.Payments.Where(p => invoices.Any(i => i.Id == p.InvoiceId)).SumAsync(p => (decimal?)p.Amount) ?? 0;
                totals.Add(new
                {
                    type, invoiceCount = await invoices.CountAsync(),
                    draftValue = await invoices.Where(x => x.Status == InvoiceStatus.Draft).SumAsync(x => (decimal?)x.Total) ?? 0,
                    invoicedValue, recordedPayments, outstanding = invoicedValue - recordedPayments
                });
            }
            result["finance"] = totals;
        }
        if (result.Count == 0) throw new ApiException(403, "A report requires the corresponding read permission.");
        return Results.Ok(result);
    }

    private static bool CanRead(AccessScope scope) => scope.CompanyWide || scope.DepartmentIds.Length > 0;

    private static async Task<IResult> Reconciliation(StockFlowDbContext db, AccessService access,
        ILoggerFactory logs, int page = 1, int pageSize = 20)
    {
        await access.RequireAsync("Inventory.Read");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.StockLots.AsNoTracking().Select(lot => new
        {
            lot.Id, lot.LotNumber, lot.ProductId,
            onHand = (decimal?)lot.Balance.OnHand,
            reserved = (decimal?)lot.Balance.Reserved,
            movementOnHand = db.StockMovements.Where(m => m.StockLotId == lot.Id).Sum(m => (decimal?)m.OnHandDelta) ?? 0,
            movementReserved = db.StockMovements.Where(m => m.StockLotId == lot.Id).Sum(m => (decimal?)m.ReservedDelta) ?? 0,
            activeReserved = db.StockReservations.Where(r => r.StockLotId == lot.Id && r.Status == ReservationStatus.Active)
                .Sum(r => (decimal?)r.Quantity) ?? 0
        }).Where(x => x.onHand == null || x.reserved == null || x.onHand != x.movementOnHand
            || x.reserved != x.movementReserved || x.reserved != x.activeReserved);
        var total = await query.CountAsync();
        if (total > 0)
        {
            ReconciliationFailures.Add(total);
            logs.CreateLogger("StockFlow.Reconciliation").LogError("Inventory reconciliation detected {MismatchCount} unbalanced lots", total);
        }
        var items = await query.OrderBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return Results.Ok(new { reconciled = total == 0, mismatchCount = total, items, page, pageSize });
    }
}
