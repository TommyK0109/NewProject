namespace CSharp2.Domain;

public enum InvoiceType { Receivable, Payable }
public enum InvoiceStatus { Draft, Issued, Paid }

public sealed class Invoice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public InvoiceType Type { get; set; }
    public Guid? SalesOrderId { get; set; }
    public Guid? GoodsReceiptId { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public decimal Total { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
    public SalesOrder? SalesOrder { get; set; }
    public GoodsReceipt? GoodsReceipt { get; set; }
    public List<InvoiceItem> Items { get; set; } = [];
    public List<Payment> Payments { get; set; } = [];
}

public sealed class InvoiceItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public Guid? SalesOrderItemId { get; set; }
    public Guid? GoodsReceiptItemId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public Invoice Invoice { get; set; } = null!;
    public SalesOrderItem? SalesOrderItem { get; set; }
    public GoodsReceiptItem? GoodsReceiptItem { get; set; }
}

public sealed class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid InvoiceId { get; set; }
    public string Reference { get; set; } = "";
    public decimal Amount { get; set; }
    public DateTime PaidAtUtc { get; set; }
    public DateTime RecordedAtUtc { get; set; }
    public Guid RecordedByUserId { get; set; }
    public Invoice Invoice { get; set; } = null!;
}
