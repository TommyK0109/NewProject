using System.Security.Claims;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Application;

public sealed record AccessScope(bool CompanyWide, Guid[] DepartmentIds)
{
    public bool Allows(Guid departmentId) => CompanyWide || DepartmentIds.Contains(departmentId);
}

public sealed class AccessService(StockFlowDbContext db, IHttpContextAccessor context)
{
    public Guid UserId => Guid.TryParse(context.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
        ? id : throw new ApiException(401, "Authentication required.");

    public async Task<AppUser> CurrentAsync()
    {
        var userId = UserId;
        var user = await db.AppUsers.AsNoTracking().Include(x => x.Employee).SingleOrDefaultAsync(x => x.Id == userId);
        var version = context.HttpContext?.User.FindFirstValue("access_version");
        if (user is null || !user.IsActive || user.Employee.EmploymentStatus != EmploymentStatus.Active
            || version != user.AccessVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ApiException(401, "The account or session is no longer active.");
        return user;
    }

    public async Task<AccessScope> ScopeAsync(string permission)
    {
        var user = await CurrentAsync();
        var company = await db.UserCompanyRoles.AnyAsync(x => x.UserId == user.Id
            && x.Role.Permissions.Any(p => p.Permission.Code == permission));
        var departments = company ? [] : await db.UserDepartmentRoles
            .Where(x => x.UserId == user.Id && x.Role.Permissions.Any(p => p.Permission.Code == permission))
            .Select(x => x.DepartmentId).Distinct().ToArrayAsync();
        return new AccessScope(company, departments);
    }

    public async Task RequireAsync(string permission, Guid? departmentId = null, bool conceal = false)
    {
        var scope = await ScopeAsync(permission);
        if (!(scope.CompanyWide || departmentId.HasValue && scope.Allows(departmentId.Value)))
            throw new ApiException(conceal ? 404 : 403, conceal ? "Resource not found." : "Permission is required for this scope.");
    }
}
