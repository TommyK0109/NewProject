namespace CSharp2.Domain;

public enum PurchaseOrderStatus { Draft, Approved, PartiallyReceived, Received, Cancelled, Closed }
public enum QualityInspectionStatus { Pending, Pass, Fail }

public sealed class PurchaseOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public Guid SupplierId { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;
    public Guid CreatedByUserId { get; set; }
    public Guid? ApprovedByUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
    public List<PurchaseOrderItem> Items { get; set; } = [];
    public Supplier Supplier { get; set; } = null!;
}

public sealed class PurchaseOrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PurchaseOrderId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal CancelledQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
    public Product Product { get; set; } = null!;
}

public sealed class GoodsReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid PostedByUserId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public List<GoodsReceiptItem> Items { get; set; } = [];
    public PurchaseOrder PurchaseOrder { get; set; } = null!;
}

public sealed class GoodsReceiptItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GoodsReceiptId { get; set; }
    public Guid PurchaseOrderItemId { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public GoodsReceipt GoodsReceipt { get; set; } = null!;
    public PurchaseOrderItem PurchaseOrderItem { get; set; } = null!;
}

public sealed class QualityInspection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StockLotId { get; set; }
    public Guid DepartmentId { get; set; }
    public QualityInspectionStatus Status { get; set; } = QualityInspectionStatus.Pending;
    public Guid? InspectedByUserId { get; set; }
    public DateTime? InspectedAtUtc { get; set; }
    public string Notes { get; set; } = "";
    public byte[] Version { get; set; } = [];
    public StockLot StockLot { get; set; } = null!;
}
