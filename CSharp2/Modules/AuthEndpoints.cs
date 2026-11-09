using System.Security.Claims;
using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        }).AllowAnonymous();

        app.MapPost("/api/auth/login", async (LoginRequest request, StockFlowDbContext db, HttpContext context) =>
        {
            var normalizedEmail = Input.Required(request.Email, 254).ToUpperInvariant();
            if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 128)
                throw new ApiException(400, "Email and password are required.");
            var user = await db.AppUsers.Include(x => x.Employee).SingleOrDefaultAsync(x => x.NormalizedEmail == normalizedEmail);
            var hasher = new PasswordHasher<AppUser>();
            var placeholder = new AppUser();
            var hash = user?.PasswordHash ?? UnknownAccountHash;
            var verified = hasher.VerifyHashedPassword(user ?? placeholder, hash, request.Password);
            if (user is null || verified == PasswordVerificationResult.Failed || !user.IsActive
                || user.Employee.EmploymentStatus != EmploymentStatus.Active)
                throw new ApiException(401, "Invalid credentials or inactive account.");
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Name, user.Email),
                new Claim("access_version", user.AccessVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            };
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok(new { user.Id, user.EmployeeId, user.Email });
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync();
            return Results.Ok(new { signedOut = true });
        }).RequireAuthorization();

        app.MapGet("/api/auth/me", async (AccessService access, StockFlowDbContext db) =>
        {
            var user = await access.CurrentAsync();
            var company = await db.UserCompanyRoles.Where(x => x.UserId == user.Id)
                .SelectMany(x => x.Role.Permissions.Select(p => p.Permission.Code)).Distinct().ToArrayAsync();
            var department = await db.UserDepartmentRoles.Where(x => x.UserId == user.Id)
                .SelectMany(x => x.Role.Permissions.Select(p => new { x.DepartmentId, Permission = p.Permission.Code }))
                .Distinct().ToArrayAsync();
            return Results.Ok(new { user.Id, user.Email, user.EmployeeId, user.Employee.DepartmentId,
                companyPermissions = company, departmentPermissions = department });
        }).RequireAuthorization();
    }

    private static readonly string UnknownAccountHash = new PasswordHasher<AppUser>()
        .HashPassword(new AppUser(), "Unknown-account-placeholder");
    public sealed record LoginRequest(string Email, string Password);
}
