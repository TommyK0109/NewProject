namespace CSharp2.Domain;

public enum ReservationStatus { Active, Released, Consumed }
public enum ShipmentStatus { Prepared, Dispatched, Delivered, Cancelled }

public sealed class StockReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SalesOrderItemId { get; set; }
    public Guid StockLotId { get; set; }
    public decimal Quantity { get; set; }
    public ReservationStatus Status { get; set; } = ReservationStatus.Active;
    public SalesOrderItem SalesOrderItem { get; set; } = null!;
    public StockLot StockLot { get; set; } = null!;
}

public sealed class Shipment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Number { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public Guid SalesOrderId { get; set; }
    public string CarrierName { get; set; } = "";
    public string TrackingNumber { get; set; } = "";
    public string DeliveryAddress { get; set; } = "";
    public string TrackingNotes { get; set; } = "";
    public ShipmentStatus Status { get; set; } = ShipmentStatus.Prepared;
    public DateTime? DispatchedAtUtc { get; set; }
    public DateTime? DeliveredAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
    public SalesOrder SalesOrder { get; set; } = null!;
    public List<ShipmentItem> Items { get; set; } = [];
}

public sealed class ShipmentItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ShipmentId { get; set; }
    public Guid StockReservationId { get; set; }
    public Shipment Shipment { get; set; } = null!;
    public StockReservation StockReservation { get; set; } = null!;
}
