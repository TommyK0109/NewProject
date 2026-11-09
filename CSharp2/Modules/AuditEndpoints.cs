using CSharp2.Application;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/audit", ListAudit).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListAudit(StockFlowDbContext db, AccessService access,
        Guid? departmentId = null, Guid? entityId = null, Guid? requestId = null, string? entityType = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, int page = 1, int pageSize = 20)
    {
        var scope = await access.ScopeAsync("Audit.Read");
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Audit.Read permission is required.");
        CatalogEndpoints.ValidatePage(page, pageSize);
        if (fromUtc > toUtc) throw new ApiException(400, "Audit date range is invalid.");
        var query = db.AuditEvents.AsNoTracking();
        if (!scope.CompanyWide)
            query = query.Where(x => x.ResourceDepartmentId.HasValue
                && scope.DepartmentIds.Contains(x.ResourceDepartmentId.Value));
        if (departmentId.HasValue) query = query.Where(x => x.ResourceDepartmentId == departmentId.Value);
        if (entityId.HasValue) query = query.Where(x => x.EntityId == entityId.Value);
        if (requestId.HasValue) query = query.Where(x => x.RequestId == requestId.Value);
        if (!string.IsNullOrWhiteSpace(entityType))
        {
            entityType = Input.Required(entityType, 100);
            query = query.Where(x => x.EntityType == entityType);
        }
        if (fromUtc.HasValue) query = query.Where(x => x.CreatedAtUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(x => x.CreatedAtUtc <= toUtc.Value);
        var total = await query.CountAsync();
        var items = await query.OrderByDescending(x => x.CreatedAtUtc).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id, x.ActorUserId, x.ActorDepartmentId, x.ResourceDepartmentId,
                x.EntityType, x.EntityId, x.Action, x.DetailsJson, x.RequestId, x.CreatedAtUtc
            }).ToListAsync();
        return Results.Ok(new { items, total, page, pageSize });
    }
}
