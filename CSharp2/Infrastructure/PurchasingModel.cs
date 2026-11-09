using CSharp2.Domain;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Infrastructure;

public sealed partial class StockFlowDbContext
{
    private static void ConfigurePurchasing(ModelBuilder model)
    {
        model.Entity<PurchaseOrder>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.DepartmentId, x.Status, x.CreatedAtUtc, x.Id });
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.Supplier).WithMany().HasForeignKey(x => x.SupplierId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.CreatedByUserId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ApprovedByUserId);
            b.HasMany(x => x.Items).WithOne(x => x.PurchaseOrder).HasForeignKey(x => x.PurchaseOrderId);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_PurchaseOrders_Status", "[Status] IN ('Draft','Approved','PartiallyReceived','Received','Cancelled','Closed')");
                t.HasCheckConstraint("CK_PurchaseOrders_Approver", "([ApprovedByUserId] IS NULL AND [ApprovedAtUtc] IS NULL AND [Status] IN ('Draft','Cancelled')) OR ([ApprovedByUserId] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL AND [ApprovedByUserId] <> [CreatedByUserId] AND [Status] IN ('Approved','PartiallyReceived','Received','Cancelled','Closed'))");
            });
        });
        model.Entity<PurchaseOrderItem>(b =>
        {
            b.Property(x => x.Quantity).HasPrecision(18, 3);
            b.Property(x => x.CancelledQuantity).HasPrecision(18, 3);
            b.Property(x => x.UnitCost).HasPrecision(18, 2);
            b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
            b.HasIndex(x => new { x.PurchaseOrderId, x.ProductId }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("CK_PurchaseOrderItems_Amounts", "[Quantity] > 0 AND [CancelledQuantity] >= 0 AND [CancelledQuantity] <= [Quantity] AND [UnitCost] >= 0"));
        });
        model.Entity<GoodsReceipt>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.PostedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.DepartmentId, x.PostedAtUtc, x.Id });
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.PurchaseOrder).WithMany().HasForeignKey(x => x.PurchaseOrderId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PostedByUserId);
            b.HasMany(x => x.Items).WithOne(x => x.GoodsReceipt).HasForeignKey(x => x.GoodsReceiptId);
        });
        model.Entity<GoodsReceiptItem>(b =>
        {
            b.Property(x => x.ReceivedQuantity).HasPrecision(18, 3);
            b.HasOne(x => x.PurchaseOrderItem).WithMany().HasForeignKey(x => x.PurchaseOrderItemId);
            b.HasIndex(x => new { x.GoodsReceiptId, x.PurchaseOrderItemId }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("CK_GoodsReceiptItems_Quantity", "[ReceivedQuantity] > 0"));
        });
        model.Entity<QualityInspection>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Notes).HasMaxLength(2000).IsRequired();
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.StockLotId).IsUnique();
            b.HasIndex(x => new { x.DepartmentId, x.Status, x.Id });
            b.HasOne(x => x.StockLot).WithMany().HasForeignKey(x => x.StockLotId);
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.InspectedByUserId);
            b.ToTable(t => t.HasCheckConstraint("CK_QualityInspections_Decision", "([Status] = 'Pending' AND [InspectedByUserId] IS NULL AND [InspectedAtUtc] IS NULL) OR ([Status] IN ('Pass','Fail') AND [InspectedByUserId] IS NOT NULL AND [InspectedAtUtc] IS NOT NULL)"));
        });
    }

    private void GuardPurchasingHistory()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is GoodsReceipt or GoodsReceiptItem
                && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Posted goods receipts are immutable.");
            if (entry.Entity is PurchaseOrder && entry.State == EntityState.Modified)
            {
                if (entry.Property(nameof(PurchaseOrder.DepartmentId)).IsModified)
                    throw new InvalidOperationException("Purchase order ownership is immutable.");
                var status = (PurchaseOrderStatus)entry.OriginalValues[nameof(PurchaseOrder.Status)]!;
                if (status != PurchaseOrderStatus.Draft
                    && (entry.Property(nameof(PurchaseOrder.Number)).IsModified || entry.Property(nameof(PurchaseOrder.SupplierId)).IsModified))
                    throw new InvalidOperationException("Approved purchase order details are immutable.");
            }
            if (entry.Entity is PurchaseOrder or QualityInspection && entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Historical documents cannot be deleted.");
            if (entry.Entity is PurchaseOrderItem line && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                var parent = ChangeTracker.Entries<PurchaseOrder>().SingleOrDefault(x => x.Entity.Id == line.PurchaseOrderId)
                    ?? throw new InvalidOperationException("Load the purchase order before changing its lines.");
                var originalStatus = parent.Property(x => x.Status).OriginalValue;
                if (originalStatus != PurchaseOrderStatus.Draft
                    && (entry.State == EntityState.Deleted || entry.Properties.Any(x => x.IsModified && x.Metadata.Name != nameof(PurchaseOrderItem.CancelledQuantity))))
                    throw new InvalidOperationException("Approved purchase order quantities and costs are immutable.");
            }
            if (entry.Entity is QualityInspection && entry.State == EntityState.Modified)
            {
                if ((QualityInspectionStatus)entry.OriginalValues[nameof(QualityInspection.Status)]! != QualityInspectionStatus.Pending)
                    throw new InvalidOperationException("Final quality decisions are immutable.");
                if (entry.Property(nameof(QualityInspection.DepartmentId)).IsModified || entry.Property(nameof(QualityInspection.StockLotId)).IsModified)
                    throw new InvalidOperationException("Inspection ownership and lot are immutable.");
            }
            if (entry.Entity is StockLot && entry.State == EntityState.Modified
                && entry.Property(nameof(StockLot.QualityStatus)).IsModified
                && (QualityStatus)entry.OriginalValues[nameof(StockLot.QualityStatus)]! != QualityStatus.Pending)
                throw new InvalidOperationException("Final lot quality is immutable.");
        }
    }
}
