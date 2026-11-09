using CSharp2.Domain;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Infrastructure;

public sealed partial class StockFlowDbContext
{
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<ShipmentItem> ShipmentItems => Set<ShipmentItem>();

    private static void ConfigureFulfilment(ModelBuilder model)
    {
        model.Entity<StockReservation>(b =>
        {
            b.Property(x => x.Quantity).HasPrecision(18, 3);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasOne(x => x.SalesOrderItem).WithMany().HasForeignKey(x => x.SalesOrderItemId);
            b.HasOne(x => x.StockLot).WithMany().HasForeignKey(x => x.StockLotId);
            b.HasIndex(x => new { x.SalesOrderItemId, x.StockLotId }).IsUnique();
            b.HasIndex(x => new { x.StockLotId, x.Status });
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_StockReservations_Quantity", "[Quantity] > 0");
                t.HasCheckConstraint("CK_StockReservations_Status", "[Status] IN ('Active','Released','Consumed')");
            });
        });
        model.Entity<Shipment>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.CarrierName).HasMaxLength(200).IsRequired();
            b.Property(x => x.TrackingNumber).HasMaxLength(200).IsRequired();
            b.Property(x => x.DeliveryAddress).HasMaxLength(1000).IsRequired();
            b.Property(x => x.TrackingNotes).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => x.SalesOrderId).IsUnique();
            b.HasIndex(x => new { x.DepartmentId, x.Status, x.Id });
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.SalesOrder).WithMany().HasForeignKey(x => x.SalesOrderId);
            b.HasMany(x => x.Items).WithOne(x => x.Shipment).HasForeignKey(x => x.ShipmentId);
            b.ToTable(t => t.HasCheckConstraint("CK_Shipments_State", "([Status] IN ('Prepared','Cancelled') AND [DispatchedAtUtc] IS NULL AND [DeliveredAtUtc] IS NULL) OR ([Status] = 'Dispatched' AND [DispatchedAtUtc] IS NOT NULL AND [DeliveredAtUtc] IS NULL) OR ([Status] = 'Delivered' AND [DispatchedAtUtc] IS NOT NULL AND [DeliveredAtUtc] IS NOT NULL AND [DeliveredAtUtc] >= [DispatchedAtUtc])"));
        });
        model.Entity<ShipmentItem>(b =>
        {
            b.HasOne(x => x.StockReservation).WithOne().HasForeignKey<ShipmentItem>(x => x.StockReservationId);
        });
    }

    private void GuardFulfilmentHistory()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is Shipment or ShipmentItem or StockReservation && entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Fulfilment history cannot be deleted.");
            if (entry.Entity is ShipmentItem && entry.State == EntityState.Modified)
                throw new InvalidOperationException("Shipment allocations are immutable.");
            if (entry.Entity is StockReservation && entry.State == EntityState.Modified
                && ((ReservationStatus)entry.OriginalValues[nameof(StockReservation.Status)]! != ReservationStatus.Active
                    || entry.Properties.Any(x => x.IsModified && x.Metadata.Name != nameof(StockReservation.Status))))
                throw new InvalidOperationException("Reservation quantities and terminal states are immutable.");
            if (entry.Entity is Shipment && entry.State == EntityState.Modified
                && entry.Properties.Any(x => x.IsModified && x.Metadata.Name is not nameof(Shipment.Status)
                    and not nameof(Shipment.DispatchedAtUtc) and not nameof(Shipment.DeliveredAtUtc)
                    and not nameof(Shipment.TrackingNotes)))
                throw new InvalidOperationException("Prepared shipment details and ownership are immutable.");
            if (entry.Entity is Shipment && entry.State == EntityState.Modified
                && entry.Property(nameof(Shipment.TrackingNotes)).IsModified
                && (ShipmentStatus)entry.OriginalValues[nameof(Shipment.Status)]! is not (ShipmentStatus.Prepared or ShipmentStatus.Dispatched))
                throw new InvalidOperationException("Tracking notes cannot change after shipment delivery or cancellation.");
        }
    }
}
