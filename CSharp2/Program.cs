using System.Diagnostics;
using System.Security.Claims;
using System.Threading.RateLimiting;
using CSharp2.Application;
using CSharp2.Infrastructure;
using CSharp2.Modules;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;

var migrate = args.Contains("--migrate");
var builder = WebApplication.CreateBuilder(args.Where(x => x != "--migrate").ToArray());
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
builder.Services.AddDbContext<StockFlowDbContext>(options => options.UseSqlServer(
    builder.Configuration.GetConnectionString("StockFlow")
        ?? throw new InvalidOperationException("ConnectionStrings:StockFlow is required.")));
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<AccessService>();
builder.Services.AddScoped<CommandExecutor>();
builder.Services.AddScoped<PurchasingService>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddDataProtection().SetApplicationName("StockFlow")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, ".local", "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Cookie.Name = "StockFlow.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var db = context.HttpContext.RequestServices.GetRequiredService<StockFlowDbContext>();
        var validId = Guid.TryParse(context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id);
        var user = validId ? await db.AppUsers.AsNoTracking().Include(x => x.Employee).SingleOrDefaultAsync(x => x.Id == id) : null;
        if (user is null || !user.IsActive || user.Employee.EmploymentStatus != CSharp2.Domain.EmploymentStatus.Active
            || context.Principal?.FindFirstValue("access_version") != user.AccessVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync();
        }
    };
});
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "StockFlow.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
        }));
});

var app = builder.Build();
if (migrate || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StockFlowDbContext>();
    await db.Database.MigrateAsync();
    await DatabaseSeed.InitializeAsync(db, app.Configuration);
    if (migrate) return;
}

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    context.Response.Headers["X-Correlation-ID"] = context.TraceIdentifier;
    using var logScope = app.Logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = context.TraceIdentifier });
    try { await next(); }
    catch (Exception exception)
    {
        var (status, detail) = exception switch
        {
            ApiException error => (error.StatusCode, error.Message),
            AntiforgeryValidationException => (400, "A valid X-CSRF-TOKEN header and CSRF cookie are required."),
            BadHttpRequestException => (400, "The request body or parameters are invalid."),
            DbUpdateConcurrencyException => (409, "The resource changed. Reload and retry."),
            DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } } => (409, "The code, account, or request already exists."),
            DbUpdateException { InnerException: SqlException { Number: 547 or 51001 } } => (409, "The change violates a database relationship or inventory rule."),
            Exception error when CommandExecutor.IsRetryable(error) => (409, "A concurrent command prevented this update. Retry with the same RequestId."),
            SqlException => (503, "The required database is unavailable."),
            _ => (500, "An unexpected error occurred.")
        };
        if (status >= 500) app.Logger.LogError(exception, "Request {Method} {Path} failed", context.Request.Method, context.Request.Path);
        if (context.Response.HasStarted) throw;
        context.Response.Clear();
        await Results.Problem(statusCode: status, detail: detail, extensions: new Dictionary<string, object?>
        {
            ["correlationId"] = context.TraceIdentifier
        }).ExecuteAsync(context);
    }
    finally
    {
        app.Logger.LogInformation("HTTP {Method} {Path} returned {StatusCode} in {ElapsedMs} ms",
            context.Request.Method, context.Request.Path, context.Response.StatusCode,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
});
if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
// Validate JSON cookie writes explicitly, including login.
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method)
        || HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});

app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
app.MapGet("/health/ready", async (StockFlowDbContext db) =>
{
    try
    {
        await db.Departments.AnyAsync();
        return Results.Ok(new { status = "ready" });
    }
    catch { return Results.StatusCode(503); }
}).AllowAnonymous();
app.MapAuthEndpoints();
app.MapPeopleEndpoints();
app.MapAccessEndpoints();
app.MapCatalogEndpoints();
app.MapInventoryEndpoints();
app.MapPurchasingEndpoints();
app.MapReceivingEndpoints();
app.MapQualityEndpoints();
app.MapSalesEndpoints();
app.MapBomEndpoints();
app.MapProductionEndpoints();
app.MapProductionPostingEndpoints();
app.MapInventoryTraceEndpoints();
app.MapFulfilmentEndpoints();
app.MapFinanceEndpoints();
app.MapReportEndpoints();
app.MapAuditEndpoints();
app.Run();

public partial class Program;
