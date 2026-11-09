using CSharp2.Application;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Modules;

public static class PeopleEndpoints
{
    public static void MapPeopleEndpoints(this IEndpointRouteBuilder app)
    {
        var departments = app.MapGroup("/api/departments").RequireAuthorization();
        departments.MapGet("", async (StockFlowDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync("Departments.Read");
            RequireAnyScope(scope);
            return Results.Ok(await db.Departments.AsNoTracking()
                .Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.Id))
                .OrderBy(x => x.Code).Select(x => new DepartmentDto(x.Id, x.Code, x.Name, x.IsActive)).ToListAsync());
        });
        departments.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            await access.RequireAsync("Departments.Read", id, true);
            var entity = await db.Departments.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id)
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(ToDto(entity));
        });
        departments.MapPost("", (DepartmentCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Departments.Create", request,
                () => access.RequireAsync("Departments.Manage"), async () =>
                {
                    var entity = new Department
                    {
                        Id = Guid.NewGuid(), Code = Input.Code(request.Code, 20), Name = Input.Required(request.Name), IsActive = true
                    };
                    db.Departments.Add(entity);
                    return await Task.FromResult(new CommandResult<DepartmentDto>(ToDto(entity), "Department", entity.Id,
                        entity.Id, "Created", new { entity.Code, entity.Name }, 201));
                }));
        departments.MapPut("/{id:guid}", (Guid id, DepartmentUpdate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Departments.Update", new { id, request },
                () => access.RequireAsync("Departments.Manage", id, true), async () =>
                {
                    var entity = await db.Departments.SingleOrDefaultAsync(x => x.Id == id)
                        ?? throw new ApiException(404, "Resource not found.");
                    var before = new { entity.Name, entity.IsActive };
                    entity.Name = Input.Required(request.Name);
                    entity.IsActive = request.IsActive;
                    return new CommandResult<DepartmentDto>(ToDto(entity), "Department", entity.Id, entity.Id,
                        "Updated", new { Before = before, After = new { entity.Name, entity.IsActive } });
                }));

        var employees = app.MapGroup("/api/employees").RequireAuthorization();
        employees.MapGet("", async (StockFlowDbContext db, AccessService access, int page = 1, int pageSize = 20, string? search = null) =>
        {
            ValidatePage(page, pageSize);
            var scope = await access.ScopeAsync("Employees.Read");
            RequireAnyScope(scope);
            var query = db.Employees.AsNoTracking().Where(x => scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId));
            if (!string.IsNullOrWhiteSpace(search))
                query = query.Where(x => x.EmployeeNumber.Contains(search) || x.FullName.Contains(search));
            var total = await query.CountAsync();
            var items = await query.OrderBy(x => x.EmployeeNumber).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new EmployeeDto(x.Id, x.EmployeeNumber, x.DepartmentId, x.FullName, x.JobTitle,
                    x.EmploymentStatus, x.StartedOn, x.EndedOn)).ToListAsync();
            return Results.Ok(new { items, total, page, pageSize });
        });
        employees.MapGet("/{id:guid}", async (Guid id, StockFlowDbContext db, AccessService access) =>
        {
            var scope = await access.ScopeAsync("Employees.Read");
            var entity = await db.Employees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id
                && (scope.CompanyWide || scope.DepartmentIds.Contains(x.DepartmentId)))
                ?? throw new ApiException(404, "Resource not found.");
            return Results.Ok(ToDto(entity));
        });
        employees.MapPost("", (EmployeeCreate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Employees.Create", request,
                () => access.RequireAsync("Employees.Manage", request.DepartmentId), async () =>
                {
                    await RequireActiveDepartment(db, request.DepartmentId);
                    var entity = new Employee
                    {
                        Id = Guid.NewGuid(), EmployeeNumber = Input.Code(request.EmployeeNumber, 40), DepartmentId = request.DepartmentId,
                        FullName = Input.Required(request.FullName), JobTitle = Input.Required(request.JobTitle),
                        EmploymentStatus = EmploymentStatus.Active, StartedOn = request.StartedOn
                    };
                    ValidateStartDate(entity.StartedOn);
                    db.Employees.Add(entity);
                    return new CommandResult<EmployeeDto>(ToDto(entity), "Employee", entity.Id, entity.DepartmentId,
                        "Created", new { entity.EmployeeNumber, entity.DepartmentId, entity.FullName, entity.JobTitle, entity.StartedOn }, 201);
                }));
        employees.MapPut("/{id:guid}", (Guid id, EmployeeUpdate request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Employees.Update", new { id, request },
                async () => await access.RequireAsync("Employees.Manage", (await FindEmployee(db, id)).DepartmentId, true), async () =>
                {
                    var entity = await FindEmployee(db, id);
                    var before = new { entity.FullName, entity.JobTitle };
                    entity.FullName = Input.Required(request.FullName);
                    entity.JobTitle = Input.Required(request.JobTitle);
                    return new CommandResult<EmployeeDto>(ToDto(entity), "Employee", entity.Id, entity.DepartmentId,
                        "Updated", new { Before = before, After = new { entity.FullName, entity.JobTitle } });
                }));
        employees.MapPost("/{id:guid}/transfer", (Guid id, EmployeeTransfer request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Employees.Transfer", new { id, request }, async () =>
                {
                    await access.RequireAsync("Employees.Manage", (await FindEmployee(db, id)).DepartmentId, true);
                    await access.RequireAsync("Employees.Manage", request.DepartmentId);
                }, async () =>
                {
                    var entity = await FindEmployee(db, id);
                    if (entity.EmploymentStatus != EmploymentStatus.Active)
                        throw new ApiException(409, "Only active employees can transfer.");
                    if (entity.DepartmentId == request.DepartmentId)
                        throw new ApiException(400, "Choose a different home department.");
                    await RequireActiveDepartment(db, request.DepartmentId);
                    var previousDepartmentId = entity.DepartmentId;
                    entity.DepartmentId = request.DepartmentId;
                    var user = await db.AppUsers.SingleOrDefaultAsync(x => x.EmployeeId == id);
                    if (user is not null)
                        await RevokeAssignments(db, user);
                    return new CommandResult<EmployeeDto>(ToDto(entity), "Employee", entity.Id, previousDepartmentId,
                        "Transferred", new { PreviousDepartmentId = previousDepartmentId, NewDepartmentId = entity.DepartmentId,
                            AllGrantsRevoked = true, ReviewRequired = true });
                }));
        employees.MapPost("/{id:guid}/offboard", (Guid id, EmployeeOffboard request, StockFlowDbContext db, AccessService access, CommandExecutor commands) =>
            commands.ExecuteAsync(request.RequestId, "Employees.Offboard", new { id, request },
                async () => await access.RequireAsync("Employees.Manage", (await FindEmployee(db, id)).DepartmentId, true), async () =>
                {
                    var entity = await FindEmployee(db, id);
                    if (entity.EmploymentStatus != EmploymentStatus.Active)
                        throw new ApiException(409, "Employee is already offboarded.");
                    if (request.EndedOn < entity.StartedOn)
                        throw new ApiException(400, "Employment end date cannot precede the start date.");
                    entity.EmploymentStatus = EmploymentStatus.Offboarded;
                    entity.EndedOn = request.EndedOn;
                    var user = await db.AppUsers.SingleOrDefaultAsync(x => x.EmployeeId == id);
                    if (user is not null)
                    {
                        user.IsActive = false;
                        await RevokeAssignments(db, user);
                    }
                    return new CommandResult<EmployeeDto>(ToDto(entity), "Employee", entity.Id, entity.DepartmentId,
                        "Offboarded", new { entity.EndedOn, AccountDisabled = user is not null, AllGrantsRevoked = true });
                }));
    }

    private static async Task<Employee> FindEmployee(StockFlowDbContext db, Guid id) =>
        await db.Employees.SingleOrDefaultAsync(x => x.Id == id) ?? throw new ApiException(404, "Resource not found.");

    private static async Task RequireActiveDepartment(StockFlowDbContext db, Guid id)
    {
        if (!await db.Departments.AnyAsync(x => x.Id == id && x.IsActive))
            throw new ApiException(400, "An active department is required.");
    }

    private static async Task RevokeAssignments(StockFlowDbContext db, AppUser user)
    {
        db.UserDepartmentRoles.RemoveRange(await db.UserDepartmentRoles.Where(x => x.UserId == user.Id).ToListAsync());
        db.UserCompanyRoles.RemoveRange(await db.UserCompanyRoles.Where(x => x.UserId == user.Id).ToListAsync());
        user.AccessVersion++;
    }

    private static void ValidateStartDate(DateOnly startedOn)
    {
        if (startedOn == default)
            throw new ApiException(400, "Employment start date is required.");
    }

    private static void ValidatePage(int page, int pageSize)
    {
        if (page < 1 || page > 1_000_000 || pageSize is < 1 or > 100)
            throw new ApiException(400, "Page must be positive and page size between 1 and 100.");
    }

    private static void RequireAnyScope(AccessScope scope)
    {
        if (!scope.CompanyWide && scope.DepartmentIds.Length == 0)
            throw new ApiException(403, "Permission is required for this scope.");
    }

    private static DepartmentDto ToDto(Department x) => new(x.Id, x.Code, x.Name, x.IsActive);
    private static EmployeeDto ToDto(Employee x) => new(x.Id, x.EmployeeNumber, x.DepartmentId, x.FullName,
        x.JobTitle, x.EmploymentStatus, x.StartedOn, x.EndedOn);

    public sealed record DepartmentDto(Guid Id, string Code, string Name, bool IsActive);
    public sealed record EmployeeDto(Guid Id, string EmployeeNumber, Guid DepartmentId, string FullName, string JobTitle,
        EmploymentStatus EmploymentStatus, DateOnly StartedOn, DateOnly? EndedOn);
    public sealed record DepartmentCreate(Guid RequestId, string Code, string Name);
    public sealed record DepartmentUpdate(Guid RequestId, string Name, bool IsActive);
    public sealed record EmployeeCreate(Guid RequestId, string EmployeeNumber, Guid DepartmentId, string FullName,
        string JobTitle, DateOnly StartedOn);
    public sealed record EmployeeUpdate(Guid RequestId, string FullName, string JobTitle);
    public sealed record EmployeeTransfer(Guid RequestId, Guid DepartmentId);
    public sealed record EmployeeOffboard(Guid RequestId, DateOnly EndedOn);
}
