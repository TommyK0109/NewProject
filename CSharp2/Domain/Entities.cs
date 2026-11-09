namespace CSharp2.Domain;

public enum EmploymentStatus { Active, Offboarded }
public enum ItemType { RawMaterial, FinishedGood }
public enum QualityStatus { Pending, Released, Rejected }
public enum StockMovementType { Receipt, OutputReceipt, MaterialIssue, Reserve, Release, Dispatch }

public sealed class Department
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Employee
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string EmployeeNumber { get; set; } = "";
    public Guid DepartmentId { get; set; }
    public string FullName { get; set; } = "";
    public string JobTitle { get; set; } = "";
    public EmploymentStatus EmploymentStatus { get; set; } = EmploymentStatus.Active;
    public DateOnly StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }
    public Department Department { get; set; } = null!;
}

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EmployeeId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int AccessVersion { get; set; } = 1;
    public Employee Employee { get; set; } = null!;
}

public sealed class Role
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public ICollection<RolePermission> Permissions { get; set; } = new List<RolePermission>();
}

public sealed class Permission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = null!;
}

public sealed class UserDepartmentRole
{
    public Guid UserId { get; set; }
    public Guid DepartmentId { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public sealed class UserCompanyRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = null!;
}

public sealed class Product
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Sku { get; set; } = "";
    public string Name { get; set; } = "";
    public ItemType ItemType { get; set; }
    public string BaseUnit { get; set; } = "";
    public decimal SellingPrice { get; set; }
    public decimal ReorderLevel { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string DeliveryAddress { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class Supplier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorUserId { get; set; }
    public Guid? ActorDepartmentId { get; set; }
    public Guid? ResourceDepartmentId { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Action { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public Guid? RequestId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class CommandReceipt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string CommandType { get; set; } = "";
    public Guid RequestId { get; set; }
    public string RequestHash { get; set; } = "";
    public string ResultEntityType { get; set; } = "";
    public Guid ResultEntityId { get; set; }
    public int StatusCode { get; set; }
    public string ResultJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
}

public sealed class StockLot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string LotNumber { get; set; } = "";
    public Guid ProductId { get; set; }
    public Guid? GoodsReceiptItemId { get; set; }
    public Guid? ProductionOutputId { get; set; }
    public QualityStatus QualityStatus { get; set; } = QualityStatus.Pending;
    public DateTime ReceivedAtUtc { get; set; }
    public byte[] Version { get; set; } = [];
    public Product Product { get; set; } = null!;
    public GoodsReceiptItem? GoodsReceiptItem { get; set; }
    public ProductionOutput? ProductionOutput { get; set; }
    public InventoryBalance Balance { get; set; } = null!;
}

public sealed class InventoryBalance
{
    public Guid StockLotId { get; set; }
    public decimal OnHand { get; set; }
    public decimal Reserved { get; set; }
    public byte[] Version { get; set; } = [];
    public StockLot StockLot { get; set; } = null!;
}

public sealed class StockMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StockLotId { get; set; }
    public StockMovementType Type { get; set; }
    public Guid? GoodsReceiptItemId { get; set; }
    public Guid? ProductionOutputId { get; set; }
    public Guid? MaterialIssueItemId { get; set; }
    public Guid? StockReservationId { get; set; }
    public Guid? ShipmentItemId { get; set; }
    public decimal OnHandDelta { get; set; }
    public decimal ReservedDelta { get; set; }
    public DateTime PostedAtUtc { get; set; }
}
