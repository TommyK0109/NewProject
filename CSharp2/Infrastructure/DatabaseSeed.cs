using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CSharp2.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Infrastructure;

public static class DatabaseSeed
{
    public static readonly (string Code, string Name)[] Departments =
    [
        ("SAL", "Sales & Customer Service"),
        ("PUR", "Purchasing"),
        ("PLN", "Production Planning"),
        ("PRD", "Production"),
        ("QC", "Quality Control"),
        ("WH", "Warehouse & Inventory"),
        ("LOG", "Logistics & Shipping"),
        ("HR", "Human Resources & Administration"),
        ("FIN", "Finance & Accounting")
    ];

    private static readonly string[] Phase2PermissionCodes =
    [
        "Purchasing.Read", "Purchasing.Create", "Purchasing.Approve", "Purchasing.Cancel",
        "Receipts.Read", "Inventory.Receive", "Quality.Read", "Quality.Inspect"
    ];

    private static readonly string[] Phase3PermissionCodes =
    [
        "Orders.Read", "Orders.Create", "Orders.Confirm", "Bom.Read", "Bom.Manage", "Bom.Approve",
        "Production.Read", "Production.Plan", "Production.Output", "Inventory.Issue"
    ];

    private static readonly string[] Phase4And5PermissionCodes =
    [
        "Orders.Cancel", "Inventory.Reserve", "Inventory.Dispatch", "Shipments.Read", "Shipments.Prepare", "Shipments.Deliver",
        "Finance.Read", "Finance.IssueInvoice", "Finance.RecordPayment"
    ];

    public static readonly string[] PermissionCodes =
    [
        "Departments.Read", "Departments.Manage", "Employees.Read", "Employees.Manage", "Access.Manage",
        "Products.Read", "Products.Manage", "Customers.Read", "Customers.Manage",
        "Suppliers.Read", "Suppliers.Manage", "Inventory.Read", "Audit.Read", .. Phase2PermissionCodes, .. Phase3PermissionCodes,
        .. Phase4And5PermissionCodes
    ];

    public static Guid StableId(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes("StockFlow/" + key)).AsSpan(0, 16));

    public static async Task InitializeAsync(StockFlowDbContext db, IConfiguration configuration)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = 'StockFlow.Seed', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; IF @result < 0 THROW 51000, 'Could not acquire seed lock.', 1;");

        var changed = false;
        foreach (var (code, name) in Departments)
        {
            var departmentId = StableId("department/" + code);
            if (!await db.Departments.AnyAsync(x => x.Code == code))
            {
                db.Departments.Add(new Department { Id = departmentId, Code = code, Name = name });
                changed = true;
            }
            if (!await db.Employees.AnyAsync(x => x.EmployeeNumber == "DEMO-" + code))
            {
                // Synthetic profiles have no login or grants. They are safe sample organization data.
                var existingDepartment = await db.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Code == code);
                db.Employees.Add(new Employee
                {
                    Id = StableId("employee/" + code),
                    EmployeeNumber = "DEMO-" + code,
                    DepartmentId = existingDepartment?.Id ?? departmentId,
                    FullName = name + " Demo Employee",
                    JobTitle = "Demonstration profile",
                    StartedOn = new DateOnly(2026, 1, 1)
                });
                changed = true;
            }
        }
        foreach (var code in PermissionCodes)
        {
            if (await db.Permissions.AnyAsync(x => x.Code == code)) continue;
            db.Permissions.Add(new Permission { Id = StableId("permission/" + code), Code = code });
            changed = true;
        }
        await db.SaveChangesAsync();

        var permissions = await db.Permissions.ToDictionaryAsync(x => x.Code);
        var roleDefinitions = new Dictionary<string, string[]>
        {
            ["SAL"] = ["Departments.Read", "Customers.Read", "Customers.Manage", "Products.Read", "Inventory.Read"],
            ["PUR"] = ["Departments.Read", "Suppliers.Read", "Suppliers.Manage", "Products.Read", "Inventory.Read"],
            ["PLN"] = ["Departments.Read", "Products.Read", "Inventory.Read"],
            ["PRD"] = ["Departments.Read", "Products.Read"],
            ["QC"] = ["Departments.Read", "Products.Read", "Inventory.Read"],
            ["WH"] = ["Departments.Read", "Products.Read", "Inventory.Read"],
            ["LOG"] = ["Departments.Read", "Customers.Read", "Products.Read", "Inventory.Read"],
            ["HR"] = ["Departments.Read", "Employees.Read", "Employees.Manage"],
            ["FIN"] = ["Departments.Read", "Suppliers.Read", "Customers.Read", "Products.Read"],
            ["ACCESS_ADMIN"] = PermissionCodes.Except(Phase2PermissionCodes).Except(Phase3PermissionCodes).Except(Phase4And5PermissionCodes).ToArray(),
            ["PURCHASER"] = ["Purchasing.Read", "Purchasing.Create", "Purchasing.Cancel"],
            ["PURCHASING_APPROVER"] = ["Purchasing.Read", "Purchasing.Approve", "Purchasing.Cancel"],
            ["GOODS_RECEIVER"] = ["Receipts.Read", "Inventory.Receive"],
            ["QUALITY_INSPECTOR"] = ["Quality.Read", "Quality.Inspect"],
            ["SALES_OPERATOR"] = ["Orders.Read", "Orders.Create", "Orders.Confirm"],
            ["BOM_EDITOR"] = ["Bom.Read", "Bom.Manage"],
            ["BOM_APPROVER"] = ["Bom.Read", "Bom.Approve"],
            ["PRODUCTION_PLANNER"] = ["Production.Read", "Production.Plan"],
            ["PRODUCTION_OPERATOR"] = ["Production.Read", "Production.Output"],
            ["MATERIAL_ISSUER"] = ["Inventory.Issue"],
            ["SALES_CANCELLER"] = ["Orders.Cancel"],
            ["RESERVATION_OPERATOR"] = ["Inventory.Reserve"],
            ["DISPATCHER"] = ["Inventory.Dispatch"],
            ["SHIPPING_OPERATOR"] = ["Shipments.Read", "Shipments.Prepare", "Shipments.Deliver"],
            ["FINANCE_OPERATOR"] = ["Finance.Read", "Finance.IssueInvoice", "Finance.RecordPayment"]
        };
        foreach (var (code, grants) in roleDefinitions)
        {
            if (await db.Roles.AnyAsync(x => x.Code == code)) continue;
            var role = new Role
            {
                Id = StableId("role/" + code),
                Code = code,
                Name = code switch
                {
                    "ACCESS_ADMIN" => "Access Administrator",
                    "PURCHASER" => "Purchasing operator",
                    "PURCHASING_APPROVER" => "Purchase order approver",
                    "GOODS_RECEIVER" => "Goods receiver",
                    "QUALITY_INSPECTOR" => "Quality inspector",
                    "SALES_OPERATOR" => "Sales operator",
                    "BOM_EDITOR" => "BOM editor",
                    "BOM_APPROVER" => "BOM approver",
                    "PRODUCTION_PLANNER" => "Production planner",
                    "PRODUCTION_OPERATOR" => "Production operator",
                    "MATERIAL_ISSUER" => "Material issuer",
                    "SALES_CANCELLER" => "Sales order cancellation operator",
                    "RESERVATION_OPERATOR" => "Finished goods reservation operator",
                    "DISPATCHER" => "Warehouse dispatcher",
                    "SHIPPING_OPERATOR" => "Shipping operator",
                    "FINANCE_OPERATOR" => "Finance operator",
                    _ => Departments.Single(x => x.Code == code).Name
                }
            };
            foreach (var grant in grants)
                role.Permissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permissions[grant].Id });
            db.Roles.Add(role);
            changed = true;
        }
        await db.SaveChangesAsync();

        var email = configuration["Bootstrap:Email"]?.Trim();
        var password = configuration["Bootstrap:Password"];
        if (!await db.AppUsers.AnyAsync() && (!string.IsNullOrWhiteSpace(email) || !string.IsNullOrEmpty(password)))
        {
            if (string.IsNullOrWhiteSpace(email) || email.Length > 254
                || !System.Net.Mail.MailAddress.TryCreate(email, out var parsedEmail) || parsedEmail.Address != email)
                throw new InvalidOperationException("Bootstrap:Email must be a valid email address.");
            if (string.IsNullOrEmpty(password) || password.Length < 12 || password.Length > 128)
                throw new InvalidOperationException("Bootstrap:Password must contain 12 to 128 characters.");

            var hr = await db.Departments.SingleAsync(x => x.Code == "HR");
            var employee = new Employee
            {
                Id = StableId("employee/bootstrap"),
                EmployeeNumber = "BOOTSTRAP-ADMIN",
                DepartmentId = hr.Id,
                FullName = "Bootstrap Administrator",
                JobTitle = "Access Administrator",
                StartedOn = DateOnly.FromDateTime(DateTime.UtcNow)
            };
            var user = new AppUser
            {
                Id = StableId("user/bootstrap"),
                EmployeeId = employee.Id,
                Email = email,
                NormalizedEmail = email.ToUpperInvariant()
            };
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
            db.Employees.Add(employee);
            db.AppUsers.Add(user);
            var adminRoleId = await db.Roles.Where(x => x.Code == "ACCESS_ADMIN").Select(x => x.Id).SingleAsync();
            db.UserCompanyRoles.Add(new UserCompanyRole { UserId = user.Id, RoleId = adminRoleId });
            db.AuditEvents.Add(new AuditEvent
            {
                EntityType = nameof(AppUser), EntityId = user.Id, Action = "Account.Bootstrap",
                ResourceDepartmentId = hr.Id,
                DetailsJson = JsonSerializer.Serialize(new { systemActor = "DatabaseSeed", employeeId = employee.Id, role = "ACCESS_ADMIN", scope = "Company" })
            });
            changed = true;
        }

        if (changed)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                EntityType = "System", EntityId = StableId("system/seed"), Action = "Database.Seed",
                DetailsJson = "{\"systemActor\":\"DatabaseSeed\",\"scope\":\"Synthetic organization and phase 1-5 access bundles\"}"
            });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
