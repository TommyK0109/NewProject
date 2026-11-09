using CSharp2.Domain;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Infrastructure;

public sealed partial class StockFlowDbContext(DbContextOptions<StockFlowDbContext> options) : DbContext(options)
{
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserDepartmentRole> UserDepartmentRoles => Set<UserDepartmentRole>();
    public DbSet<UserCompanyRole> UserCompanyRoles => Set<UserCompanyRole>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<CommandReceipt> CommandReceipts => Set<CommandReceipt>();
    public DbSet<StockLot> StockLots => Set<StockLot>();
    public DbSet<InventoryBalance> InventoryBalances => Set<InventoryBalance>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<GoodsReceiptItem> GoodsReceiptItems => Set<GoodsReceiptItem>();
    public DbSet<ProductionOutput> ProductionOutputs => Set<ProductionOutput>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderItem> PurchaseOrderItems => Set<PurchaseOrderItem>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<QualityInspection> QualityInspections => Set<QualityInspection>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderItem> SalesOrderItems => Set<SalesOrderItem>();
    public DbSet<BillOfMaterial> BillOfMaterials => Set<BillOfMaterial>();
    public DbSet<BomItem> BomItems => Set<BomItem>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<ProductionRequirement> ProductionRequirements => Set<ProductionRequirement>();
    public DbSet<MaterialIssue> MaterialIssues => Set<MaterialIssue>();
    public DbSet<MaterialIssueItem> MaterialIssueItems => Set<MaterialIssueItem>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Department>(b =>
        {
            b.Property(x => x.Code).HasMaxLength(20).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
        model.Entity<Employee>(b =>
        {
            b.Property(x => x.EmployeeNumber).HasMaxLength(40).IsRequired();
            b.HasIndex(x => x.EmployeeNumber).IsUnique();
            b.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            b.Property(x => x.JobTitle).HasMaxLength(200).IsRequired();
            b.Property(x => x.EmploymentStatus).HasConversion<string>().HasMaxLength(20);
            b.HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasIndex(x => new { x.DepartmentId, x.EmploymentStatus, x.EmployeeNumber });
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Employees_Status", "[EmploymentStatus] IN ('Active','Offboarded')");
                t.HasCheckConstraint("CK_Employees_Dates", "([EmploymentStatus] = 'Active' AND [EndedOn] IS NULL) OR ([EmploymentStatus] = 'Offboarded' AND [EndedOn] IS NOT NULL AND [EndedOn] >= [StartedOn])");
            });
        });
        model.Entity<AppUser>(b =>
        {
            b.Property(x => x.Email).HasMaxLength(254).IsRequired();
            b.Property(x => x.NormalizedEmail).HasMaxLength(254).IsRequired();
            b.Property(x => x.PasswordHash).HasMaxLength(1000).IsRequired();
            b.HasIndex(x => x.NormalizedEmail).IsUnique();
            b.HasIndex(x => x.EmployeeId).IsUnique();
            b.HasOne(x => x.Employee).WithOne().HasForeignKey<AppUser>(x => x.EmployeeId);
            b.ToTable(t => t.HasCheckConstraint("CK_AppUsers_AccessVersion", "[AccessVersion] > 0"));
        });
        model.Entity<Role>(b =>
        {
            b.Property(x => x.Code).HasMaxLength(50).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
            b.HasMany(x => x.Permissions).WithOne().HasForeignKey(x => x.RoleId);
        });
        model.Entity<Permission>(b =>
        {
            b.Property(x => x.Code).HasMaxLength(100).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
        });
        model.Entity<RolePermission>(b =>
        {
            b.HasKey(x => new { x.RoleId, x.PermissionId });
            b.HasOne(x => x.Permission).WithMany().HasForeignKey(x => x.PermissionId);
        });
        model.Entity<UserDepartmentRole>(b =>
        {
            b.HasKey(x => new { x.UserId, x.DepartmentId, x.RoleId });
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId);
            b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
            b.HasIndex(x => new { x.DepartmentId, x.UserId });
        });
        model.Entity<UserCompanyRole>(b =>
        {
            b.HasKey(x => new { x.UserId, x.RoleId });
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
            b.HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId);
        });
        model.Entity<Product>(b =>
        {
            b.Property(x => x.Sku).HasMaxLength(60).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.BaseUnit).HasMaxLength(30).IsRequired();
            b.Property(x => x.ItemType).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.SellingPrice).HasPrecision(18, 2);
            b.Property(x => x.ReorderLevel).HasPrecision(18, 3);
            b.HasIndex(x => x.Sku).IsUnique();
            b.HasIndex(x => new { x.ItemType, x.IsActive, x.Sku });
            b.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Products_Type", "[ItemType] IN ('RawMaterial','FinishedGood')");
                t.HasCheckConstraint("CK_Products_Amounts", "[SellingPrice] >= 0 AND [ReorderLevel] >= 0");
            });
        });
        model.Entity<Customer>(b =>
        {
            b.Property(x => x.Code).HasMaxLength(60).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.DeliveryAddress).HasMaxLength(1000).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
        });
        model.Entity<Supplier>(b =>
        {
            b.Property(x => x.Code).HasMaxLength(60).IsRequired();
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.HasIndex(x => x.Code).IsUnique();
        });
        model.Entity<AuditEvent>(b =>
        {
            b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.DetailsJson).IsRequired();
            b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.ActorUserId);
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.ActorDepartmentId);
            b.HasOne<Department>().WithMany().HasForeignKey(x => x.ResourceDepartmentId);
            b.HasIndex(x => new { x.ResourceDepartmentId, x.CreatedAtUtc, x.Id });
            b.HasIndex(x => new { x.EntityType, x.EntityId, x.CreatedAtUtc });
            b.ToTable(t => t.HasCheckConstraint("CK_AuditEvents_Json", "ISJSON([DetailsJson]) = 1"));
        });
        model.Entity<CommandReceipt>(b =>
        {
            b.Property(x => x.CommandType).HasMaxLength(100).IsRequired();
            b.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            b.Property(x => x.ResultEntityType).HasMaxLength(100).IsRequired();
            b.Property(x => x.ResultJson).IsRequired();
            b.Property(x => x.CreatedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.CommandType, x.RequestId }).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("CK_CommandReceipts_Json", "ISJSON([ResultJson]) = 1"));
        });
        model.Entity<StockLot>(b =>
        {
            b.Property(x => x.LotNumber).HasMaxLength(80).IsRequired();
            b.Property(x => x.QualityStatus).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ReceivedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.LotNumber).IsUnique();
            b.HasIndex(x => x.GoodsReceiptItemId).IsUnique().HasFilter("[GoodsReceiptItemId] IS NOT NULL");
            b.HasIndex(x => x.ProductionOutputId).IsUnique().HasFilter("[ProductionOutputId] IS NOT NULL");
            b.HasIndex(x => new { x.ProductId, x.QualityStatus, x.ReceivedAtUtc, x.Id });
            b.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId);
            b.HasOne(x => x.GoodsReceiptItem).WithMany().HasForeignKey(x => x.GoodsReceiptItemId);
            b.HasOne(x => x.ProductionOutput).WithMany().HasForeignKey(x => x.ProductionOutputId);
            b.ToTable(t =>
            {
                t.HasTrigger("TR_StockLots_QualityGuard");
                t.HasCheckConstraint("CK_StockLots_Quality", "[QualityStatus] IN ('Pending','Released','Rejected')");
                t.HasCheckConstraint("CK_StockLots_Source", "([GoodsReceiptItemId] IS NOT NULL AND [ProductionOutputId] IS NULL) OR ([GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NOT NULL)");
            });
        });
        model.Entity<InventoryBalance>(b =>
        {
            b.HasKey(x => x.StockLotId);
            b.Property(x => x.OnHand).HasPrecision(18, 3);
            b.Property(x => x.Reserved).HasPrecision(18, 3);
            b.Property(x => x.Version).IsRowVersion();
            b.HasOne(x => x.StockLot).WithOne(x => x.Balance).HasForeignKey<InventoryBalance>(x => x.StockLotId);
            b.ToTable(t =>
            {
                t.HasTrigger("TR_InventoryBalances_QualityGuard");
                t.HasCheckConstraint("CK_InventoryBalances_Bounds", "[OnHand] >= 0 AND [Reserved] >= 0 AND [Reserved] <= [OnHand]");
            });
        });
        model.Entity<StockMovement>(b =>
        {
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(x => x.OnHandDelta).HasPrecision(18, 3);
            b.Property(x => x.ReservedDelta).HasPrecision(18, 3);
            b.Property(x => x.PostedAtUtc).HasDefaultValueSql("SYSUTCDATETIME()");
            b.HasOne<StockLot>().WithMany().HasForeignKey(x => x.StockLotId);
            b.HasOne<GoodsReceiptItem>().WithMany().HasForeignKey(x => x.GoodsReceiptItemId);
            b.HasOne<ProductionOutput>().WithMany().HasForeignKey(x => x.ProductionOutputId);
            b.HasOne<MaterialIssueItem>().WithMany().HasForeignKey(x => x.MaterialIssueItemId);
            b.HasOne<StockReservation>().WithMany().HasForeignKey(x => x.StockReservationId);
            b.HasOne<ShipmentItem>().WithMany().HasForeignKey(x => x.ShipmentItemId);
            b.HasIndex(x => new { x.StockLotId, x.PostedAtUtc, x.Id });
            b.HasIndex(x => new { x.GoodsReceiptItemId, x.Type }).IsUnique().HasFilter("[GoodsReceiptItemId] IS NOT NULL");
            b.HasIndex(x => new { x.ProductionOutputId, x.Type }).IsUnique().HasFilter("[ProductionOutputId] IS NOT NULL");
            b.HasIndex(x => new { x.MaterialIssueItemId, x.Type }).IsUnique().HasFilter("[MaterialIssueItemId] IS NOT NULL");
            b.HasIndex(x => new { x.StockReservationId, x.Type }).IsUnique().HasFilter("[StockReservationId] IS NOT NULL");
            b.HasIndex(x => new { x.ShipmentItemId, x.Type }).IsUnique().HasFilter("[ShipmentItemId] IS NOT NULL");
            b.ToTable(t => t.HasCheckConstraint("CK_StockMovements_SourceAndDeltas", """
                ([ReservedDelta] = 0 AND [StockReservationId] IS NULL AND [ShipmentItemId] IS NULL AND
                  (([Type] = 'Receipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NOT NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NULL)
                  OR ([Type] = 'OutputReceipt' AND [OnHandDelta] > 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NOT NULL AND [MaterialIssueItemId] IS NULL)
                  OR ([Type] = 'MaterialIssue' AND [OnHandDelta] < 0 AND [GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NOT NULL)))
                OR ([GoodsReceiptItemId] IS NULL AND [ProductionOutputId] IS NULL AND [MaterialIssueItemId] IS NULL AND
                  (([Type] = 'Reserve' AND [OnHandDelta] = 0 AND [ReservedDelta] > 0 AND [StockReservationId] IS NOT NULL AND [ShipmentItemId] IS NULL)
                  OR ([Type] = 'Release' AND [OnHandDelta] = 0 AND [ReservedDelta] < 0 AND [StockReservationId] IS NOT NULL AND [ShipmentItemId] IS NULL)
                  OR ([Type] = 'Dispatch' AND [OnHandDelta] < 0 AND [ReservedDelta] = [OnHandDelta] AND [StockReservationId] IS NULL AND [ShipmentItemId] IS NOT NULL)))
                """));
        });

        ConfigurePurchasing(model);
        ConfigureManufacturing(model);
        ConfigureFulfilment(model);
        ConfigureFinance(model);

        foreach (var foreignKey in model.Model.GetEntityTypes().SelectMany(x => x.GetForeignKeys()))
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardHistory();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardHistory();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void GuardHistory()
    {
        GuardPurchasingHistory();
        GuardManufacturingHistory();
        GuardFulfilmentHistory();
        GuardFinanceHistory();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is AuditEvent or CommandReceipt or StockMovement
                && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Audit, command receipts, and stock movements are append-only.");

            if (entry.State == EntityState.Deleted
                && entry.Entity is Department or Employee or AppUser or Product or Customer or Supplier or StockLot or InventoryBalance)
                throw new InvalidOperationException("Historical master data and inventory cannot be deleted; deactivate the record instead.");
        }
    }
}
