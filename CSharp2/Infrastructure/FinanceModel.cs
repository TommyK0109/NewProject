using CSharp2.Domain;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Infrastructure;

public sealed partial class StockFlowDbContext
{
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<InvoiceItem> InvoiceItems => Set<InvoiceItem>();
    public DbSet<Payment> Payments => Set<Payment>();

    private static void ConfigureFinance(ModelBuilder model)
    {
        model.Entity<Invoice>(b =>
        {
            b.Property(x => x.Number).HasMaxLength(60).IsRequired();
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Total).HasPrecision(18, 2);
            b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.Number).IsUnique();
            b.HasIndex(x => x.SalesOrderId).IsUnique().HasFilter("[SalesOrderId] IS NOT NULL");
            b.HasIndex(x => x.GoodsReceiptId).IsUnique().HasFilter("[GoodsReceiptId] IS NOT NULL");
            b.HasIndex(x => new { x.DepartmentId, x.Status, x.CreatedAtUtc, x.Id });
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.SalesOrder).WithMany().HasForeignKey(x => x.SalesOrderId);
            b.HasOne(x => x.GoodsReceipt).WithMany().HasForeignKey(x => x.GoodsReceiptId);
            b.HasMany(x => x.Items).WithOne(x => x.Invoice).HasForeignKey(x => x.InvoiceId);
            b.HasMany(x => x.Payments).WithOne(x => x.Invoice).HasForeignKey(x => x.InvoiceId);
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Invoices_Source", "([Type] = 'Receivable' AND [SalesOrderId] IS NOT NULL AND [GoodsReceiptId] IS NULL) OR ([Type] = 'Payable' AND [SalesOrderId] IS NULL AND [GoodsReceiptId] IS NOT NULL)");
                t.HasCheckConstraint("CK_Invoices_Status", "([Status] = 'Draft' AND [IssuedAtUtc] IS NULL) OR ([Status] IN ('Issued','Paid') AND [IssuedAtUtc] IS NOT NULL)");
                t.HasCheckConstraint("CK_Invoices_Total", "[Total] >= 0");
            });
        });
        model.Entity<InvoiceItem>(b =>
        {
            b.Property(x => x.Quantity).HasPrecision(18, 3);
            b.Property(x => x.UnitPrice).HasPrecision(18, 2);
            b.Property(x => x.LineTotal).HasPrecision(18, 2);
            b.HasOne(x => x.SalesOrderItem).WithMany().HasForeignKey(x => x.SalesOrderItemId);
            b.HasOne(x => x.GoodsReceiptItem).WithMany().HasForeignKey(x => x.GoodsReceiptItemId);
            b.HasIndex(x => new { x.InvoiceId, x.SalesOrderItemId }).IsUnique().HasFilter("[SalesOrderItemId] IS NOT NULL");
            b.HasIndex(x => new { x.InvoiceId, x.GoodsReceiptItemId }).IsUnique().HasFilter("[GoodsReceiptItemId] IS NOT NULL");
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_InvoiceItems_Source", "([SalesOrderItemId] IS NOT NULL AND [GoodsReceiptItemId] IS NULL) OR ([SalesOrderItemId] IS NULL AND [GoodsReceiptItemId] IS NOT NULL)");
                t.HasCheckConstraint("CK_InvoiceItems_Amounts", "[Quantity] > 0 AND [UnitPrice] >= 0 AND [LineTotal] >= 0 AND [LineTotal] = ROUND([Quantity] * [UnitPrice], 2)");
            });
        });
        model.Entity<Payment>(b =>
        {
            b.Property(x => x.Reference).HasMaxLength(100).IsRequired();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.RecordedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasIndex(x => x.Reference).IsUnique();
            b.HasIndex(x => new { x.InvoiceId, x.RecordedAtUtc, x.Id });
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RecordedByUserId);
            b.ToTable(t => t.HasCheckConstraint("CK_Payments_Amount", "[Amount] > 0"));
        });
    }

    private void GuardFinanceHistory()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is Payment && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Recorded payments are immutable.");
            if (entry.Entity is Invoice && entry.State == EntityState.Deleted)
                throw new InvalidOperationException("Historical invoices cannot be deleted.");
            if (entry.Entity is Invoice && entry.State == EntityState.Modified)
            {
                if (new[] { nameof(Invoice.DepartmentId), nameof(Invoice.Type), nameof(Invoice.SalesOrderId), nameof(Invoice.GoodsReceiptId), nameof(Invoice.CreatedAtUtc) }
                    .Any(name => entry.Property(name).IsModified))
                    throw new InvalidOperationException("Invoice ownership and source are immutable.");
                var status = (InvoiceStatus)entry.OriginalValues[nameof(Invoice.Status)]!;
                if (status != InvoiceStatus.Draft && entry.Properties.Any(x => x.IsModified && x.Metadata.Name != nameof(Invoice.Status)))
                    throw new InvalidOperationException("Issued invoice details are immutable.");
                if (status == InvoiceStatus.Paid || status == InvoiceStatus.Issued && (InvoiceStatus)entry.CurrentValues[nameof(Invoice.Status)]! != InvoiceStatus.Paid)
                    throw new InvalidOperationException("Only settlement may change an issued invoice.");
            }
            if (entry.Entity is InvoiceItem item && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            {
                var parent = ChangeTracker.Entries<Invoice>().SingleOrDefault(x => x.Entity.Id == item.InvoiceId)
                    ?? throw new InvalidOperationException("Load the invoice before changing its lines.");
                if (parent.State != EntityState.Added && parent.Property(x => x.Status).OriginalValue != InvoiceStatus.Draft)
                    throw new InvalidOperationException("Issued invoice lines are immutable.");
            }
        }
    }
}
