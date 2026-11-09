using System.Net.Mail;
using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class AccessEndpoints
{
    public static void MapAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var accessRoutes = app.MapGroup("/api/access").RequireAuthorization();
        accessRoutes.MapGet("/permissions", async (StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Access.Manage");
            return Results.Ok(await db.Permissions.AsNoTracking().OrderBy(x => x.Code)
                .Select(x => new { x.Id, x.Code }).ToListAsync());
        });
        accessRoutes.MapGet("/roles", async (StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Access.Manage");
            return Results.Ok(await db.Roles.AsNoTracking().OrderBy(x => x.Code)
                .Select(x => new { x.Id, x.Code, x.Name, PermissionCodes = x.Permissions.Select(p => p.Permission.Code).ToList() })
                .ToListAsync());
        });
        accessRoutes.MapPost("/roles", (RoleCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Access.CreateRole", request,
                () => access.RequireAsync("Access.Manage"), async () =>
                {
                    var codes = request.PermissionCodes?.Select(x => Input.Required(x, 100)).Distinct().ToArray() ?? [];
                    if (codes.Length == 0)
                        throw new ApiException(400, "A role must contain at least one permission.");
                    var permissions = await db.Permissions.Where(x => codes.Contains(x.Code)).ToListAsync();
                    if (permissions.Count != codes.Length)
                        throw new ApiException(400, "One or more permission codes are invalid.");
                    var role = new Role { Id = Guid.NewGuid(), Code = Input.Code(request.Code), Name = Input.Required(request.Name) };
                    db.Roles.Add(role);
                    foreach (var permission in permissions)
                        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id });
                    var value = new RoleDto(role.Id, role.Code, role.Name, codes);
                    return new CommandResult<RoleDto>(value, "Role", role.Id, null, "RoleCreated", new { role.Code, PermissionCodes = codes }, 201);
                }));
        accessRoutes.MapGet("/users", async (StockFlowDbContext db, AccessService access, int page = 1, int pageSize = 20) =>
        {
            await access.RequireAsync("Access.Manage");
            if (page < 1 || page > 1_000_000 || pageSize is < 1 or > 100)
                throw new ApiException(400, "Page must be positive and page size between 1 and 100.");
            var query = db.AppUsers.AsNoTracking();
            var total = await query.CountAsync();
            var items = await query.OrderBy(x => x.Email).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new AccountDto(x.Id, x.EmployeeId, x.Email, x.IsActive)).ToListAsync();
            return Results.Ok(new { items, total, page, pageSize });
        });
        accessRoutes.MapGet("/users/{id:guid}/grants", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Access.Manage");
            await FindUser(db, id);
            var department = await db.UserDepartmentRoles.AsNoTracking().Where(x => x.UserId == id)
                .Select(x => new { x.RoleId, x.DepartmentId }).ToListAsync();
            var company = await db.UserCompanyRoles.AsNoTracking().Where(x => x.UserId == id)
                .Select(x => new { x.RoleId }).ToListAsync();
            return Results.Ok(new { department, company });
        });
        accessRoutes.MapPost("/users/{id:guid}/grants", (Guid id, GrantRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            ChangeGrant(id, request, true, db, access, commands));
        accessRoutes.MapPost("/users/{id:guid}/grants/revoke", (Guid id, GrantRequest request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            ChangeGrant(id, request, false, db, access, commands));

        app.MapPost("/api/employees/{id:guid}/account", (Guid id, AccountCreate request, StockFlowDbContext db,
                AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Access.ProvisionAccount", new { id, request },
                () => access.RequireAsync("Access.Manage"), async () =>
                {
                    var employee = await db.Employees.SingleOrDefaultAsync(x => x.Id == id)
                        ?? throw new ApiException(404, "Resource not found.");
                    if (employee.EmploymentStatus != EmploymentStatus.Active)
                        throw new ApiException(409, "Only active employees may receive an account.");
                    if (await db.AppUsers.AnyAsync(x => x.EmployeeId == id))
                        throw new ApiException(409, "Employee already has an account.");
                    var email = ValidateEmail(request.Email);
                    var user = new AppUser
                    {
                        Id = Guid.NewGuid(), EmployeeId = id, Email = email, NormalizedEmail = email.ToUpperInvariant(),
                        IsActive = true, AccessVersion = 1
                    };
                    SetPassword(user, request.Password);
                    db.AppUsers.Add(user);
                    return new CommandResult<AccountDto>(ToDto(user), "AppUser", user.Id, employee.DepartmentId,
                        "AccountProvisioned", new { user.EmployeeId, user.Email }, 201);
                })).RequireAuthorization();
        accessRoutes.MapPost("/users/{id:guid}/status", (Guid id, AccountStatus request, StockFlowDbContext db,
                AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Access.ChangeAccountStatus", new { id, request },
                () => access.RequireAsync("Access.Manage"), async () =>
                {
                    var user = await FindUser(db, id);
                    if (request.IsActive && user.Employee.EmploymentStatus != EmploymentStatus.Active)
                        throw new ApiException(409, "An offboarded employee cannot sign in.");
                    user.IsActive = request.IsActive;
                    user.AccessVersion++;
                    return new CommandResult<AccountDto>(ToDto(user), "AppUser", id, user.Employee.DepartmentId,
                        "AccountStatusChanged", new { user.IsActive });
                }));
        accessRoutes.MapPost("/users/{id:guid}/password-reset", (Guid id, PasswordReset request, StockFlowDbContext db,
                AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Access.ResetPassword", new { id, request },
                () => access.RequireAsync("Access.Manage"), async () =>
                {
                    var user = await FindUser(db, id);
                    SetPassword(user, request.NewPassword);
                    user.AccessVersion++;
                    return new CommandResult<AccountDto>(ToDto(user), "AppUser", id, user.Employee.DepartmentId,
                        "PasswordReset", new { user.EmployeeId });
                }));
    }

    private static Task<IResult> ChangeGrant(Guid userId, GrantRequest request, bool assign, StockFlowDbContext db,
        AccessService access, CommandExecutor commands) =>
        commands.ExecuteAsync(request.RequestId, assign ? "Access.AssignRole" : "Access.RevokeRole", new { userId, request },
            () => access.RequireAsync("Access.Manage"), async () =>
            {
                var user = await FindUser(db, userId);
                if (!Enum.IsDefined(request.Scope)
                    || (request.Scope == GrantScope.Department && (request.DepartmentId is null || request.DepartmentId == Guid.Empty))
                    || (request.Scope == GrantScope.Company && request.DepartmentId is not null))
                    throw new ApiException(400, "Choose Company without a department, or Department with a department ID.");
                if (!await db.Roles.AnyAsync(x => x.Id == request.RoleId))
                    throw new ApiException(400, "Role not found.");
                if (assign && (!user.IsActive || user.Employee.EmploymentStatus != EmploymentStatus.Active))
                    throw new ApiException(409, "Roles may only be assigned to active accounts and employees.");
                if (request.Scope == GrantScope.Department)
                {
                    if (!await db.Departments.AnyAsync(x => x.Id == request.DepartmentId && (!assign || x.IsActive)))
                        throw new ApiException(400, "Department not found or inactive.");
                    var existing = await db.UserDepartmentRoles.SingleOrDefaultAsync(x => x.UserId == userId
                        && x.RoleId == request.RoleId && x.DepartmentId == request.DepartmentId);
                    if (assign && existing is null)
                        db.UserDepartmentRoles.Add(new UserDepartmentRole
                        {
                            UserId = userId, RoleId = request.RoleId, DepartmentId = request.DepartmentId!.Value
                        });
                    if (!assign && existing is not null)
                        db.UserDepartmentRoles.Remove(existing);
                }
                else
                {
                    var existing = await db.UserCompanyRoles.SingleOrDefaultAsync(x => x.UserId == userId && x.RoleId == request.RoleId);
                    if (assign && existing is null)
                        db.UserCompanyRoles.Add(new UserCompanyRole { UserId = userId, RoleId = request.RoleId });
                    if (!assign && existing is not null)
                        db.UserCompanyRoles.Remove(existing);
                }
                user.AccessVersion++;
                var result = new GrantDto(userId, request.RoleId, request.Scope, request.DepartmentId, assign);
                return new CommandResult<GrantDto>(result, "AppUser", userId, request.DepartmentId,
                    assign ? "RoleAssigned" : "RoleRevoked", result);
            });

    private static async Task<AppUser> FindUser(StockFlowDbContext db, Guid id) =>
        await db.AppUsers.Include(x => x.Employee).SingleOrDefaultAsync(x => x.Id == id)
        ?? throw new ApiException(404, "Resource not found.");

    private static string ValidateEmail(string email)
    {
        email = Input.Required(email, 254);
        if (!MailAddress.TryCreate(email, out var address) || !string.Equals(address.Address, email, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(400, "A valid email address is required.");
        return email;
    }

    private static void SetPassword(AppUser user, string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length is < 12 or > 128)
            throw new ApiException(400, "Password must contain 12 to 128 characters.");
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
    }

    private static AccountDto ToDto(AppUser x) => new(x.Id, x.EmployeeId, x.Email, x.IsActive);

    public enum GrantScope { Department, Company }
    public sealed record RoleCreate(Guid RequestId, string Code, string Name, string[] PermissionCodes);
    public sealed record RoleDto(Guid Id, string Code, string Name, string[] PermissionCodes);
    public sealed record AccountDto(Guid Id, Guid EmployeeId, string Email, bool IsActive);
    public sealed record AccountCreate(Guid RequestId, string Email, string Password);
    public sealed record AccountStatus(Guid RequestId, bool IsActive);
    public sealed record PasswordReset(Guid RequestId, string NewPassword);
    public sealed record GrantRequest(Guid RequestId, Guid RoleId, GrantScope Scope, Guid? DepartmentId);
    public sealed record GrantDto(Guid UserId, Guid RoleId, GrantScope Scope, Guid? DepartmentId, bool Assigned);
}
