namespace CSharp2.Domain;

public enum SalesOrderStatus { Draft, Confirmed, Reserved, Dispatched, Completed, Cancelled }
public enum BomStatus { Draft, Approved }
public enum ProductionOrderStatus { Draft, Released, InProduction, AwaitingQC, Completed, Cancelled }

public sealed class SalesOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public Guid CustomerId { get; set; }
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;
    public DateTime RequiredByUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
    public List<SalesOrderItem> Items { get; set; } = [];
    public Customer Customer { get; set; } = null!;
}

public sealed class SalesOrderItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SalesOrderId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public SalesOrder SalesOrder { get; set; } = null!;
    public Product Product { get; set; } = null!;
}

public sealed class BillOfMaterial
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FinishedProductId { get; set; }
    public int Revision { get; set; }
    public BomStatus Status { get; set; } = BomStatus.Draft;
    public Guid? ApprovedByUserId { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
    public List<BomItem> Items { get; set; } = [];
    public Product FinishedProduct { get; set; } = null!;
}

public sealed class BomItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BillOfMaterialId { get; set; }
    public Guid MaterialProductId { get; set; }
    public decimal QuantityPerUnit { get; set; }
    public BillOfMaterial BillOfMaterial { get; set; } = null!;
    public Product MaterialProduct { get; set; } = null!;
}

public sealed class ProductionOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public Guid SalesOrderItemId { get; set; }
    public Guid BillOfMaterialId { get; set; }
    public decimal PlannedQuantity { get; set; }
    public DateTime ScheduledStartUtc { get; set; }
    public DateTime ScheduledEndUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public ProductionOrderStatus Status { get; set; } = ProductionOrderStatus.Draft;
    public byte[] Version { get; set; } = [];
    public List<ProductionRequirement> Requirements { get; set; } = [];
    public SalesOrderItem SalesOrderItem { get; set; } = null!;
    public BillOfMaterial BillOfMaterial { get; set; } = null!;
}

public sealed class ProductionRequirement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductionOrderId { get; set; }
    public Guid MaterialProductId { get; set; }
    public decimal RequiredQuantity { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;
    public Product MaterialProduct { get; set; } = null!;
}

public sealed class MaterialIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductionOrderId { get; set; }
    public Guid PostedByUserId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public List<MaterialIssueItem> Items { get; set; } = [];
    public ProductionOrder ProductionOrder { get; set; } = null!;
}

public sealed class MaterialIssueItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MaterialIssueId { get; set; }
    public Guid ProductionRequirementId { get; set; }
    public Guid StockLotId { get; set; }
    public decimal Quantity { get; set; }
    public MaterialIssue MaterialIssue { get; set; } = null!;
    public ProductionRequirement ProductionRequirement { get; set; } = null!;
    public StockLot StockLot { get; set; } = null!;
}

public sealed class ProductionOutput
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProductionOrderId { get; set; }
    public decimal ProducedQuantity { get; set; }
    public decimal ScrapQuantity { get; set; }
    public Guid PostedByUserId { get; set; }
    public DateTime PostedAtUtc { get; set; }
    public ProductionOrder ProductionOrder { get; set; } = null!;
}
