using CSharp2.Domain;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Infrastructure;

public sealed partial class StockFlowDbContext
{
    private static void ConfigureManufacturing(ModelBuilder model)
    {
        model.Entity<SalesOrder>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.DepartmentId, x.Status, x.RequiredByUtc, x.Id });
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            b.HasMany(x => x.Items).WithOne(x => x.SalesOrder).HasForeignKey(x => x.SalesOrderId);
            b.ToTable(t => t.HasCheckConstraint("CK_SalesOrders_Status", "[Status] IN ('Draft','Confirmed','Reserved','Dispatched','Completed','Cancelled')"));
        });
        model.Entity<SalesOrderItem>(b =>
        {
            b.Property(x => x.Quantity).HasPrecision(18, 3);
            b.Property(x => x.UnitPrice).HasPrecision(18, 2);
            b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
            b.HasIndex(x => new { x.SalesOrderId, x.ProductId }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("CK_SalesOrderItems_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0"));
        });
        model.Entity<BillOfMaterial>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Version).IsRowVersion();
            b.HasOne(x => x.FinishedProduct).WithMany().HasForeignKey(x => x.FinishedProductId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ApprovedByUserId);
            b.HasMany(x => x.Items).WithOne(x => x.BillOfMaterial).HasForeignKey(x => x.BillOfMaterialId);
            b.HasIndex(x => new { x.FinishedProductId, x.Revision }).IsUnique();
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_BillOfMaterials_Revision", "[Revision] > 0");
                t.HasCheckConstraint("CK_BillOfMaterials_Approval", "([Status] = 'Draft' AND [ApprovedByUserId] IS NULL AND [ApprovedAtUtc] IS NULL) OR ([Status] = 'Approved' AND [ApprovedByUserId] IS NOT NULL AND [ApprovedAtUtc] IS NOT NULL)");
            });
        });
        model.Entity<BomItem>(b =>
        {
            b.Property(x => x.QuantityPerUnit).HasPrecision(18, 3);
            b.HasOne(x => x.MaterialProduct).WithMany().HasForeignKey(x => x.MaterialProductId);
            b.HasIndex(x => new { x.BillOfMaterialId, x.MaterialProductId }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("CK_BomItems_Quantity", "[QuantityPerUnit] > 0"));
        });
        model.Entity<ProductionOrder>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.PlannedQuantity).HasPrecision(18, 3);
            b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => new { x.DepartmentId, x.Status, x.ScheduledStartUtc, x.Id });
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.SalesOrderItem).WithMany().HasForeignKey(x => x.SalesOrderItemId);
            b.HasOne(x => x.BillOfMaterial).WithMany().HasForeignKey(x => x.BillOfMaterialId);
            b.HasMany(x => x.Requirements).WithOne(x => x.ProductionOrder).HasForeignKey(x => x.ProductionOrderId);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_ProductionOrders_Quantity", "[PlannedQuantity] > 0");
                t.HasCheckConstraint("CK_ProductionOrders_Schedule", "[ScheduledEndUtc] >= [ScheduledStartUtc]");
                t.HasCheckConstraint("CK_ProductionOrders_Status", "[Status] IN ('Draft','Released','InProduction','AwaitingQC','Completed','Cancelled')");
            });
        });
        model.Entity<ProductionRequirement>(b =>
        {
            b.Property(x => x.RequiredQuantity).HasPrecision(18, 3);
            b.HasOne(x => x.MaterialProduct).WithMany().HasForeignKey(x => x.MaterialProductId);
            b.HasIndex(x => new { x.ProductionOrderId, x.MaterialProductId }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("CK_ProductionRequirements_Quantity", "[RequiredQuantity] > 0"));
        });
        model.Entity<MaterialIssue>(b =>
        {
            b.Property(x => x.PostedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasIndex(x => x.ProductionOrderId).IsUnique();
            b.HasOne(x => x.ProductionOrder).WithMany().HasForeignKey(x => x.ProductionOrderId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PostedByUserId);
            b.HasMany(x => x.Items).WithOne(x => x.MaterialIssue).HasForeignKey(x => x.MaterialIssueId);
        });
        model.Entity<MaterialIssueItem>(b =>
        {
            b.Property(x => x.Quantity).HasPrecision(18, 3);
            b.HasOne(x => x.ProductionRequirement).WithMany().HasForeignKey(x => x.ProductionRequirementId);
            b.HasOne(x => x.StockLot).WithMany().HasForeignKey(x => x.StockLotId);
            b.HasIndex(x => new { x.MaterialIssueId, x.ProductionRequirementId, x.StockLotId }).IsUnique();
            b.HasIndex(x => new { x.StockLotId, x.MaterialIssueId });
            b.ToTable(t => t.HasCheckConstraint("CK_MaterialIssueItems_Quantity", "[Quantity] > 0"));
        });
        model.Entity<ProductionOutput>(b =>
        {
            b.Property(x => x.ProducedQuantity).HasPrecision(18, 3);
            b.Property(x => x.ScrapQuantity).HasPrecision(18, 3);
            b.Property(x => x.PostedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasIndex(x => x.ProductionOrderId).IsUnique();
            b.HasOne(x => x.ProductionOrder).WithMany().HasForeignKey(x => x.ProductionOrderId);
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.PostedByUserId);
            b.ToTable(t => t.HasCheckConstraint("CK_ProductionOutputs_Quantities", "[ProducedQuantity] >= 0 AND [ScrapQuantity] >= 0 AND [ProducedQuantity] + [ScrapQuantity] > 0"));
        });
    }

    private void GuardManufacturingHistory()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is MaterialIssue or MaterialIssueItem or ProductionOutput or ProductionRequirement
                && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Material issues, final output, and requirement snapshots are immutable.");
            if (entry.Entity is SalesOrder or BillOfMaterial or ProductionOrder && entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Historical documents cannot be deleted.");
            if (entry.Entity is SalesOrder && entry.State == EntityState.Modified)
            {
                if (entry.Property(nameof(SalesOrder.DepartmentId)).IsModified)
                    throw new InvalidOperationException("Sales order ownership is immutable.");
                if ((SalesOrderStatus)entry.OriginalValues[nameof(SalesOrder.Status)]! != SalesOrderStatus.Draft
                    && entry.Properties.Any(x => x.IsModified && x.Metadata.Name != nameof(SalesOrder.Status)))
                    throw new InvalidOperationException("Confirmed sales order details are immutable.");
            }
            if (entry.Entity is SalesOrderItem line && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                var parent = ChangeTracker.Entries<SalesOrder>().SingleOrDefault(x => x.Entity.Id == line.SalesOrderId);
                if (parent is null || parent.Property(x => x.Status).OriginalValue != SalesOrderStatus.Draft)
                    throw new InvalidOperationException("Confirmed sales lines are immutable.");
            }
            if (entry.Entity is BillOfMaterial && entry.State == EntityState.Modified)
            {
                if ((BomStatus)entry.OriginalValues[nameof(BillOfMaterial.Status)]! == BomStatus.Approved)
                    throw new InvalidOperationException("Approved BOM revisions are immutable.");
                if (entry.Property(nameof(BillOfMaterial.FinishedProductId)).IsModified || entry.Property(nameof(BillOfMaterial.Revision)).IsModified)
                    throw new InvalidOperationException("BOM product and revision identity are immutable.");
            }
            if (entry.Entity is BomItem item && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                var parent = ChangeTracker.Entries<BillOfMaterial>().SingleOrDefault(x => x.Entity.Id == item.BillOfMaterialId);
                if (parent is null || parent.Property(x => x.Status).OriginalValue != BomStatus.Draft)
                    throw new InvalidOperationException("Approved BOM items are immutable.");
            }
            if (entry.Entity is ProductionOrder && entry.State == EntityState.Modified
                && entry.Properties.Any(x => x.IsModified && x.Metadata.Name != nameof(ProductionOrder.Status)))
                throw new InvalidOperationException("Production plan snapshots are immutable; cancel an unstarted order to replan.");
        }
    }
}
