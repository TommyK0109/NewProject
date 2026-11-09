using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var invoices = endpoints.MapGroup("/api/invoices").RequireAuthorization();
        invoices.MapGet("/", List);
        invoices.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await RequireInvoice(db, access, id, "Finance.Read");
            return Results.Ok(await View(db, id));
        });
        invoices.MapPost("/", (InvoiceCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Invoice.Create", request, async () =>
            {
                await access.RequireAsync("Finance.IssueInvoice", request.DepartmentId);
                await RequireSource(db, access, request.Type, request.SalesOrderId, request.GoodsReceiptId);
            }, async () =>
            {
                if (!await db.Departments.AnyAsync(x => x.Id == request.DepartmentId && x.IsActive))
                    throw new ApiException(400, "An active owning department is required.");
                var invoice = new Invoice
                {
                    Number = Input.Code(request.Number, 60), DepartmentId = request.DepartmentId, Type = request.Type,
                    SalesOrderId = request.SalesOrderId, GoodsReceiptId = request.GoodsReceiptId
                };
                var lines = await SourceLines(db, invoice);
                invoice.Items = lines.Select(x => new InvoiceItem
                {
                    InvoiceId = invoice.Id, SalesOrderItemId = x.SalesOrderItemId, GoodsReceiptItemId = x.GoodsReceiptItemId,
                    Quantity = x.Quantity, UnitPrice = x.UnitPrice, LineTotal = LineAmount(x.Quantity, x.UnitPrice)
                }).ToList();
                invoice.Total = InvoiceTotal(invoice.Items.Select(x => x.LineTotal));
                db.Invoices.Add(invoice);
                await db.SaveChangesAsync();
                return await Result(db, invoice, "Invoice.Created", 201);
            }));
        invoices.MapPut("/{id:guid}", (Guid id, InvoiceUpdate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Invoice.Update", new { id, request },
                () => RequireInvoice(db, access, id, "Finance.IssueInvoice"), async () =>
                {
                    var invoice = await LockedInvoice(db, id);
                    RequireVersion(invoice, request.Version);
                    if (invoice.Status != InvoiceStatus.Draft) throw new ApiException(409, "Only Draft invoices can be edited.");
                    invoice.Number = Input.Code(request.Number, 60);
                    db.Entry(invoice).Property(x => x.Number).IsModified = true;
                    await db.SaveChangesAsync();
                    return await Result(db, invoice, "Invoice.Updated");
                }));
        invoices.MapPost("/{id:guid}/issue", (Guid id, InvoiceTransition request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Invoice.Issue", new { id, request }, async () =>
            {
                await RequireInvoice(db, access, id, "Finance.IssueInvoice");
                var source = await db.Invoices.AsNoTracking().SingleAsync(x => x.Id == id);
                await RequireSource(db, access, source.Type, source.SalesOrderId, source.GoodsReceiptId);
            }, async () =>
            {
                var invoice = await LockedInvoice(db, id);
                RequireVersion(invoice, request.Version);
                if (invoice.Status != InvoiceStatus.Draft) throw new ApiException(409, "Only a Draft invoice can be issued.");
                var source = await SourceLines(db, invoice);
                await db.Entry(invoice).Collection(x => x.Items).LoadAsync();
                if (invoice.Items.Count != source.Length || source.Any(x => !invoice.Items.Any(line =>
                    line.SalesOrderItemId == x.SalesOrderItemId && line.GoodsReceiptItemId == x.GoodsReceiptItemId
                    && line.Quantity == x.Quantity && line.UnitPrice == x.UnitPrice && line.LineTotal == LineAmount(x.Quantity, x.UnitPrice)))
                    || invoice.Total != InvoiceTotal(invoice.Items.Select(x => x.LineTotal)))
                    throw new ApiException(409, "Invoice lines must cover the full source at its saved quantities and prices.");
                invoice.IssuedAtUtc = await Now(db);
                invoice.Status = invoice.Total == 0 ? InvoiceStatus.Paid : InvoiceStatus.Issued;
                await db.SaveChangesAsync();
                return await Result(db, invoice, "Invoice.Issued");
            }));
        invoices.MapPost("/{id:guid}/payments", (Guid id, PaymentCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Payment.Record", new { id, request },
                () => RequireInvoice(db, access, id, "Finance.RecordPayment"), async () =>
                {
                    var invoice = await LockedInvoice(db, id);
                    if (invoice.Status != InvoiceStatus.Issued) throw new ApiException(409, "Payments require an issued, outstanding invoice.");
                    var amount = Input.Money(request.Amount);
                    if (amount == 0) throw new ApiException(400, "Payment amount must be positive.");
                    if (request.PaidAtUtc == default || request.PaidAtUtc.Kind != DateTimeKind.Utc)
                        throw new ApiException(400, "PaidAtUtc must be explicitly expressed in UTC (Z).");
                    var paid = await db.Payments.Where(x => x.InvoiceId == id).SumAsync(x => (decimal?)x.Amount) ?? 0;
                    if (amount > invoice.Total - paid) throw new ApiException(409, "Payment exceeds the outstanding amount.");
                    var payment = new Payment
                    {
                        InvoiceId = id, Reference = Input.Code(request.Reference, 100), Amount = amount,
                        PaidAtUtc = request.PaidAtUtc, RecordedAtUtc = await Now(db), RecordedByUserId = access.UserId
                    };
                    db.Payments.Add(payment);
                    if (paid + amount == invoice.Total) invoice.Status = InvoiceStatus.Paid;
                    await db.SaveChangesAsync();
                    var value = await View(db, id);
                    return new CommandResult<InvoiceView>(value, nameof(Payment), payment.Id, invoice.DepartmentId,
                        "Payment.Recorded", new { invoiceId = id, payment.Id, payment.Reference, payment.Amount, payment.PaidAtUtc }, 201);
                }));
        return endpoints;
    }

    private static async Task<IResult> List(StockFlowDbContext db, AccessService access,
        Guid? departmentId = null, InvoiceType? type = null, InvoiceStatus? status = null,
        string? search = null, int page = 1, int pageSize = 20)
    {
        var scope = await access.ScopeAsync("Finance.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0) throw new ApiException(403, "Permission is required for this scope.");
        CatalogEndpoints.ValidatePage(page, pageSize);
        var query = db.Invoices.AsNoTracking().Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId));
        if (departmentId.HasValue) query = query.Where(x => x.DepartmentId == departmentId);
        if (type.HasValue)
        {
            if (!Enum.IsDefined(type.Value)) throw new ApiException(400, "Invalid invoice type.");
            query = query.Where(x => x.Type == type);
        }
        if (status.HasValue)
        {
            if (!Enum.IsDefined(status.Value)) throw new ApiException(400, "Invalid invoice status.");
            query = query.Where(x => x.Status == status);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            search = Input.Required(search, 200);
            query = query.Where(x => x.Number.Contains(search));
        }
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id, x.Number, x.DepartmentId, x.Type, x.SalesOrderId, x.GoodsReceiptId, x.Status, x.Total,
                paid = x.Payments.Sum(p => (decimal?)p.Amount) ?? 0,
                outstanding = x.Status == InvoiceStatus.Draft ? 0 : x.Total - (x.Payments.Sum(p => (decimal?)p.Amount) ?? 0),
                x.CreatedAtUtc, x.IssuedAtUtc, x.Version
            }).ToArrayAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }

    private static async Task RequireInvoice(StockFlowDbContext db, AccessService access, Guid id, string permission)
    {
        var scope = await access.ScopeAsync(permission);
        if (!await db.Invoices.AnyAsync(x => x.Id == id && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
            throw new ApiException(404, "Resource not found.");
    }

    private static async Task RequireSource(StockFlowDbContext db, AccessService access, InvoiceType type, Guid? salesId, Guid? receiptId)
    {
        if (type == InvoiceType.Receivable && salesId.HasValue && !receiptId.HasValue)
        {
            var scope = await access.ScopeAsync("Orders.Read");
            if (!await db.SalesOrders.AnyAsync(x => x.Id == salesId && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
                throw new ApiException(404, "Resource not found.");
        }
        else if (type == InvoiceType.Payable && receiptId.HasValue && !salesId.HasValue)
        {
            var scope = await access.ScopeAsync("Receipts.Read");
            if (!await db.GoodsReceipts.AnyAsync(x => x.Id == receiptId && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId))))
                throw new ApiException(404, "Resource not found.");
        }
        else throw new ApiException(400, "An invoice must have exactly one source matching its type.");
    }

    private static async Task<SourceLine[]> SourceLines(StockFlowDbContext db, Invoice invoice)
    {
        if (invoice.Type == InvoiceType.Receivable)
        {
            var order = await db.SalesOrders.FromSqlInterpolated($"SELECT * FROM SalesOrders WITH (UPDLOCK,HOLDLOCK) WHERE Id = {invoice.SalesOrderId}")
                .SingleAsync();
            if (order.Status is not (SalesOrderStatus.Dispatched or SalesOrderStatus.Completed))
                throw new ApiException(409, "A customer invoice requires a dispatched sales order.");
            return await db.SalesOrderItems.Where(x => x.SalesOrderId == order.Id)
                .Select(x => new SourceLine(x.Id, null, x.Quantity, x.UnitPrice)).ToArrayAsync();
        }
        var receipt = await db.GoodsReceipts.FromSqlInterpolated($"SELECT * FROM GoodsReceipts WITH (UPDLOCK,HOLDLOCK) WHERE Id = {invoice.GoodsReceiptId}")
            .SingleAsync();
        var lines = await db.GoodsReceiptItems.Where(x => x.GoodsReceiptId == receipt.Id)
            .Select(x => new SourceLine(null, x.Id, x.ReceivedQuantity, x.PurchaseOrderItem.UnitCost)).ToArrayAsync();
        var ids = lines.Select(x => x.GoodsReceiptItemId!.Value).ToArray();
        if (ids.Length == 0 || await db.StockLots.CountAsync(x => x.GoodsReceiptItemId.HasValue
            && ids.Contains(x.GoodsReceiptItemId.Value) && x.QualityStatus == QualityStatus.Released) != ids.Length)
            throw new ApiException(409, "Every lot of a supplier receipt must pass QC before invoicing.");
        return lines;
    }

    private static Task<Invoice> LockedInvoice(StockFlowDbContext db, Guid id) => db.Invoices
        .FromSqlInterpolated($"SELECT * FROM Invoices WITH (UPDLOCK,HOLDLOCK) WHERE Id = {id}").SingleAsync();

    private static void RequireVersion(Invoice invoice, byte[]? version)
    {
        if (version is not { Length: 8 } || !invoice.Version.SequenceEqual(version))
            throw new ApiException(409, "The invoice changed. Reload and supply its current Version.");
    }

    private static decimal LineAmount(decimal quantity, decimal unitPrice)
    {
        try { return Input.Money(decimal.Round(checked(quantity * unitPrice), 2, MidpointRounding.AwayFromZero)); }
        catch (OverflowException) { throw new ApiException(400, "Invoice line amount exceeds the monetary range."); }
    }

    private static decimal InvoiceTotal(IEnumerable<decimal> amounts)
    {
        try { return Input.Money(amounts.Sum()); }
        catch (OverflowException) { throw new ApiException(400, "Invoice total exceeds the monetary range."); }
    }

    private static async Task<DateTime> Now(StockFlowDbContext db) =>
        DateTime.SpecifyKind(await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync(), DateTimeKind.Utc);

    private static async Task<InvoiceView> View(StockFlowDbContext db, Guid id)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(x => x.Items).Include(x => x.Payments).AsSplitQuery().SingleAsync(x => x.Id == id);
        var paid = invoice.Payments.Sum(x => x.Amount);
        return new InvoiceView(invoice.Id, invoice.Number, invoice.DepartmentId, invoice.Type, invoice.SalesOrderId, invoice.GoodsReceiptId,
            invoice.Status, invoice.Total, paid, invoice.Status == InvoiceStatus.Draft ? 0 : invoice.Total - paid,
            DateTime.SpecifyKind(invoice.CreatedAtUtc, DateTimeKind.Utc), invoice.IssuedAtUtc.HasValue ? DateTime.SpecifyKind(invoice.IssuedAtUtc.Value, DateTimeKind.Utc) : null,
            invoice.Version, invoice.Items.OrderBy(x => x.Id).Select(x => new InvoiceLineView(x.Id, x.SalesOrderItemId, x.GoodsReceiptItemId, x.Quantity, x.UnitPrice, x.LineTotal)).ToArray(),
            invoice.Payments.OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Id).Select(x => new PaymentView(x.Id, x.Reference, x.Amount,
                DateTime.SpecifyKind(x.PaidAtUtc, DateTimeKind.Utc), DateTime.SpecifyKind(x.RecordedAtUtc, DateTimeKind.Utc), x.RecordedByUserId)).ToArray());
    }

    private static async Task<CommandResult<InvoiceView>> Result(StockFlowDbContext db, Invoice invoice, string action, int status = 200)
    {
        var view = await View(db, invoice.Id);
        return new(view, nameof(Invoice), invoice.Id, invoice.DepartmentId, action,
            new { invoice.Number, invoice.Type, invoice.SalesOrderId, invoice.GoodsReceiptId, invoice.Status, invoice.Total }, status);
    }

    private sealed record SourceLine(Guid? SalesOrderItemId, Guid? GoodsReceiptItemId, decimal Quantity, decimal UnitPrice);
    public sealed record InvoiceCreate(Guid RequestId, string Number, Guid DepartmentId, InvoiceType Type, Guid? SalesOrderId, Guid? GoodsReceiptId);
    public sealed record InvoiceUpdate(Guid RequestId, string Number, byte[]? Version);
    public sealed record InvoiceTransition(Guid RequestId, byte[]? Version);
    public sealed record PaymentCreate(Guid RequestId, string Reference, decimal Amount, DateTime PaidAtUtc);
    public sealed record InvoiceLineView(Guid Id, Guid? SalesOrderItemId, Guid? GoodsReceiptItemId, decimal Quantity, decimal UnitPrice, decimal LineTotal);
    public sealed record PaymentView(Guid Id, string Reference, decimal Amount, DateTime PaidAtUtc, DateTime RecordedAtUtc, Guid RecordedByUserId);
    public sealed record InvoiceView(Guid Id, string Number, Guid DepartmentId, InvoiceType Type, Guid? SalesOrderId, Guid? GoodsReceiptId,
        InvoiceStatus Status, decimal Total, decimal Paid, decimal Outstanding, DateTime CreatedAtUtc, DateTime? IssuedAtUtc,
        byte[] Version, InvoiceLineView[] Items, PaymentView[] Payments);
}
