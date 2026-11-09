using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace CSharp2.Application;

public sealed record CommandResult<T>(T Value, string EntityType, Guid EntityId, Guid? DepartmentId,
    string Action, object Details, int StatusCode = 200);

public sealed class CommandExecutor(StockFlowDbContext db, AccessService access, ILogger<CommandExecutor> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly Meter Meter = new("StockFlow.Commands");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("stockflow.command.failures");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("stockflow.command.duration", "ms");

    public async Task<IResult> ExecuteAsync<T>(Guid requestId, string commandType, object payload,
        Func<Task> authorize, Func<Task<CommandResult<T>>> operation)
    {
        if (requestId == Guid.Empty) throw new ApiException(400, "RequestId is required.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, Json)));
        var userId = access.UserId;
        var started = Stopwatch.GetTimestamp();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                    // SQL owns the request lock, including across parallel app instances.
                    var resource = $"StockFlow:{userId}:{commandType}:{requestId}";
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        DECLARE @result int;
                        EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                            @LockOwner='Transaction', @LockTimeout=10000;
                        IF @result < 0 THROW 51000, 'Command lock unavailable.', 1;
                        """);
                    var actor = await access.CurrentAsync();
                    await authorize();
                    // The per-request application lock protects this immutable receipt. Avoid holding
                    // Serializable gap locks that make unrelated first-time receipt inserts deadlock.
                    var previous = await db.CommandReceipts.FromSqlInterpolated($"""
                        SELECT * FROM [CommandReceipts] WITH (READCOMMITTEDLOCK)
                        WHERE [UserId] = {userId} AND [CommandType] = {commandType} AND [RequestId] = {requestId}
                        """).SingleOrDefaultAsync();
                    if (previous is not null)
                    {
                        if (previous.RequestHash != hash)
                            throw new ApiException(409, "RequestId was already used with a different payload or target.");
                        await transaction.CommitAsync();
                        return Results.Content(previous.ResultJson, "application/json", statusCode: previous.StatusCode);
                    }

                    var result = await operation();
                    var json = JsonSerializer.Serialize(result.Value, Json);
                    var timestamp = await db.Database.SqlQueryRaw<DateTime>("SELECT SYSUTCDATETIME() AS [Value]").SingleAsync();
                    db.AuditEvents.Add(new AuditEvent
                    {
                        Id = Guid.NewGuid(), ActorUserId = userId, ActorDepartmentId = actor.Employee.DepartmentId,
                        ResourceDepartmentId = result.DepartmentId, EntityType = result.EntityType,
                        EntityId = result.EntityId, Action = result.Action,
                        DetailsJson = JsonSerializer.Serialize(result.Details, Json), RequestId = requestId, CreatedAtUtc = timestamp
                    });
                    db.CommandReceipts.Add(new CommandReceipt
                    {
                        Id = Guid.NewGuid(), UserId = userId, CommandType = commandType, RequestId = requestId,
                        RequestHash = hash, ResultEntityType = result.EntityType, ResultEntityId = result.EntityId,
                        ResultJson = json, StatusCode = result.StatusCode, CreatedAtUtc = timestamp
                    });
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();
                    logger.LogInformation("Command {CommandType} {RequestId} committed for {EntityType} {EntityId}",
                        commandType, requestId, result.EntityType, result.EntityId);
                    return Results.Content(json, "application/json", statusCode: result.StatusCode);
                }
                catch (Exception ex) when (attempt < 2 && IsRetryable(ex))
                {
                    db.ChangeTracker.Clear();
                    await Task.Delay(30 * (attempt + 1));
                }
            }
        }
        catch
        {
            Failures.Add(1, new KeyValuePair<string, object?>("command", commandType));
            throw;
        }
        finally
        {
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("command", commandType));
        }
    }

    internal static bool IsRetryable(Exception exception) =>
        exception.GetBaseException() is SqlException { Number: 1205 or 51000 };
}
