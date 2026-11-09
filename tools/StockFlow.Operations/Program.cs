using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using CSharp2.Domain;
using CSharp2.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var seconds = args.Length > 0 ? int.Parse(args[0]) : 900;
if (seconds is < 1 or > 3600) throw new ArgumentOutOfRangeException(nameof(seconds));
var outputPath = Path.GetFullPath(args.Length > 1 ? args[1] : "TestResults/phase5-operations.json");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var stopPath = Path.ChangeExtension(outputPath, ".stop");
if (File.Exists(stopPath)) throw new InvalidOperationException("Remove the previous benchmark stop file before starting another run.");
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var suffix = Guid.NewGuid().ToString("N");
var databaseName = "StockFlow_Operations_" + suffix;
var restoredName = "StockFlow_Restore_" + suffix;
var source = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("STOCKFLOW_TEST_CONNECTION")
    ?? "Server=localhost;Integrated Security=true;Encrypt=true;TrustServerCertificate=true");
source.InitialCatalog = databaseName;
var master = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = "master" };
var options = new DbContextOptionsBuilder<StockFlowDbContext>().UseSqlServer(source.ConnectionString).Options;
var password = "Load-" + Guid.NewGuid().ToString("N") + "#2026";
var users = new List<string>();
Process? server = null;
var apiLog = new ConcurrentQueue<string>();
var runStarted = DateTime.UtcNow;
try
{
    Console.WriteLine($"Preparing disposable benchmark database {databaseName}");
    await using (var db = new StockFlowDbContext(options))
    {
        await db.Database.MigrateAsync();
        await DatabaseSeed.InitializeAsync(db, new ConfigurationBuilder().Build());
        var role = new Role { Code = "LOAD-ALL", Name = "Isolated benchmark permissions" };
        role.Permissions = await db.Permissions.Select(p => new RolePermission { RoleId = role.Id, PermissionId = p.Id }).ToListAsync();
        db.Roles.Add(role);
        for (var i = 0; i < 30; i++)
        {
            var employee = new Employee { EmployeeNumber = "LOAD-" + i, FullName = "Synthetic load user " + i,
                DepartmentId = DatabaseSeed.StableId("department/SAL"), JobTitle = "Benchmark", StartedOn = new DateOnly(2026, 1, 1) };
            var user = new AppUser { EmployeeId = employee.Id, Email = $"load-{i}@stockflow.test", NormalizedEmail = $"LOAD-{i}@STOCKFLOW.TEST" };
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, password);
            db.Employees.Add(employee); db.AppUsers.Add(user);
            db.UserCompanyRoles.Add(new UserCompanyRole { UserId = user.Id, RoleId = role.Id });
            users.Add(user.Email);
        }
        await db.SaveChangesAsync();
        await using var connection = new SqlConnection(source.ConnectionString);
        await connection.OpenAsync();
        await using var seed = connection.CreateCommand();
        seed.CommandTimeout = 600;
        seed.CommandText = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "SeedLoad.sql"));
        var actors = await db.AppUsers.OrderBy(x => x.Email).Select(x => x.Id).ToArrayAsync();
        seed.Parameters.AddWithValue("@actor", actors[0]); seed.Parameters.AddWithValue("@approver", actors[1]); seed.Parameters.AddWithValue("@inspector", actors[2]);
        foreach (var (name, code) in new[] { ("sales", "SAL"), ("purchasing", "PUR"), ("warehouse", "WH"), ("quality", "QC"), ("production", "PRD") })
            seed.Parameters.AddWithValue("@" + name, DatabaseSeed.StableId("department/" + code));
        await seed.ExecuteNonQueryAsync();
    }
    var initial = await Counts(source.ConnectionString);
    if (initial["Products"] != 10000 || initial["StockLots"] != 50000 || initial["SalesOrders"] != 100000 || initial["StockMovements"] != 250000)
        throw new InvalidOperationException("Benchmark seed did not reach the required scale.");
    var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start();
    var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
    var url = $"http://127.0.0.1:{port}";
    var appAssembly = typeof(StockFlowDbContext).Assembly.Location;
    var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(appAssembly)! };
    start.ArgumentList.Add(appAssembly); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    start.Environment["ConnectionStrings__StockFlow"] = source.ConnectionString;
    start.Environment["Logging__LogLevel__Default"] = "Warning";
    start.Environment["Logging__LogLevel__Microsoft"] = "Warning";
    server = Process.Start(start) ?? throw new InvalidOperationException("Could not start benchmark API.");
    void Capture(string? line)
    {
        if (line is null) return;
        apiLog.Enqueue(line);
        while (apiLog.Count > 2000) apiLog.TryDequeue(out _);
    }
    server.OutputDataReceived += (_, e) => Capture(e.Data); server.ErrorDataReceived += (_, e) => Capture(e.Data);
    server.BeginOutputReadLine(); server.BeginErrorReadLine();
    using var probe = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
    var ready = false;
    for (var i = 0; i < 120; i++)
    {
        if (server.HasExited) throw new InvalidOperationException("Benchmark API exited before readiness.");
        try { ready = (await probe.GetAsync("/health/ready")).IsSuccessStatusCode; } catch (HttpRequestException) { }
        if (ready) break;
        await Task.Delay(250);
    }
    if (!ready) throw new InvalidOperationException("Benchmark API did not become ready.");
    var clients = new List<HttpClient>();
    for (var i = 0; i < users.Count; i++)
    {
        // Login throttling is per-IP. Spread 30 local logins across its minute window.
        if (i > 0 && i % 10 == 0) { Console.WriteLine("Waiting for the login rate-limit window before the next ten sessions"); await Task.Delay(TimeSpan.FromSeconds(61)); }
        var client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer() }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
        await Csrf(client);
        (await client.PostAsJsonAsync("/api/auth/login", new { email = users[i], password })).EnsureSuccessStatusCode();
        await Csrf(client); clients.Add(client);
    }
    Guid customerId; Guid[] products;
    await using (var db = new StockFlowDbContext(options))
    {
        customerId = await db.Customers.Select(x => x.Id).FirstAsync();
        products = await db.Products.Where(x => x.ItemType == ItemType.FinishedGood).OrderBy(x => x.Sku).Select(x => x.Id).Take(30).ToArrayAsync();
    }
    var readings = new ConcurrentBag<double>(); var postings = new ConcurrentBag<double>();
    var oneLinePostings = new ConcurrentBag<double>(); var twentyLinePostings = new ConcurrentBag<double>();
    var errors = new ConcurrentDictionary<int, long>(); long total = 0;
    var workload = Stopwatch.StartNew();
    var progressLock = new object();
    void WriteProgress()
    {
        lock (progressLock)
        {
            var snapshot = new { elapsedSeconds = workload.Elapsed.TotalSeconds, requests = Interlocked.Read(ref total),
                reads = readings.Count, postings = postings.Count, errors };
            File.WriteAllText(Path.ChangeExtension(outputPath, ".progress.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllLines(Path.ChangeExtension(outputPath, ".api.log"), apiLog);
            Console.WriteLine($"Workload {snapshot.elapsedSeconds:F0}s: {snapshot.requests} requests, {snapshot.reads} reads, {snapshot.postings} postings; statuses {JsonSerializer.Serialize(errors)}");
        }
    }
    using var progress = new Timer(_ => WriteProgress(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    Console.WriteLine($"Running {seconds} seconds with 30 authenticated users on Kestrel");
    await Task.WhenAll(clients.Select((client, worker) => Task.Run(async () =>
    {
        for (var iteration = 0; workload.Elapsed.TotalSeconds < seconds && !cancellation.IsCancellationRequested && !File.Exists(stopPath); iteration++)
        {
            var posting = iteration % 5 == 0;
            var lineCount = iteration % 10 == 0 ? 20 : 1;
            var timer = Stopwatch.StartNew();
            try
            {
                using var response = posting
                    ? await client.PostAsJsonAsync("/api/sales-orders", new
                    {
                        requestId = Guid.NewGuid(), number = $"LOAD-HTTP-{worker}-{iteration}",
                        departmentId = DatabaseSeed.StableId("department/SAL"), customerId,
                        requiredByUtc = DateTime.UtcNow.AddDays(30), items = Enumerable.Range(0, lineCount)
                            .Select(offset => new { productId = products[(worker + offset) % products.Length], quantity = 1m }).ToArray()
                    })
                    : await client.GetAsync((iteration % 3) switch
                    {
                        0 => $"/api/inventory/lots?page={1 + iteration % 100}&pageSize=20",
                        1 => $"/api/sales-orders?page={1 + iteration % 100}&pageSize=20",
                        _ => $"/api/inventory/products?page={1 + iteration % 100}&pageSize=20"
                    });
                await response.Content.ReadAsByteArrayAsync();
                if (!response.IsSuccessStatusCode) errors.AddOrUpdate((int)response.StatusCode, 1, (_, count) => count + 1);
            }
            catch (HttpRequestException) { errors.AddOrUpdate(0, 1, (_, count) => count + 1); }
            catch (TaskCanceledException) { errors.AddOrUpdate(0, 1, (_, count) => count + 1); }
            (posting ? postings : readings).Add(timer.Elapsed.TotalMilliseconds);
            if (posting) (lineCount == 1 ? oneLinePostings : twentyLinePostings).Add(timer.Elapsed.TotalMilliseconds);
            Interlocked.Increment(ref total);
            await Task.Delay(250);
        }
    })));
    workload.Stop();
    progress.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    WriteProgress();
    if (cancellation.IsCancellationRequested || File.Exists(stopPath))
    {
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(new
        {
            cancelled = true, initialCounts = initial, workloadSeconds = workload.Elapsed.TotalSeconds, requests = total,
            reads = readings.Count, postings = postings.Count, readP95Milliseconds = P95(readings),
            postingP95Milliseconds = P95(postings), errors, loadTargetMet = false, restoreCompleted = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = 1;
        return;
    }
    var reconciliation = await (await clients[0].GetAsync("/api/reports/reconciliation")).Content.ReadFromJsonAsync<JsonElement>();
    if (!reconciliation.GetProperty("reconciled").GetBoolean()) throw new InvalidOperationException("Benchmark ended with unbalanced inventory.");
    foreach (var client in clients) client.Dispose();
    server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); server.Dispose(); server = null;
    Console.WriteLine("Verifying backup and restoring into a fresh isolated database");
    var restoreTimer = Stopwatch.StartNew();
    string backupPath;
    await using (var connection = new SqlConnection(master.ConnectionString))
    {
        await connection.OpenAsync();
        await using var backup = connection.CreateCommand(); backup.CommandTimeout = 600;
        backup.CommandText = "DECLARE @path nvarchar(4000)=CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(4000)); SELECT @path;";
        var directory = (string?)await backup.ExecuteScalarAsync() ?? throw new InvalidOperationException("SQL Server's default backup directory is unavailable.");
        backupPath = directory.TrimEnd('\\', '/') + (directory.Contains('\\') ? "\\" : "/") + databaseName + ".bak";
        backup.CommandText = $"BACKUP DATABASE [{databaseName}] TO DISK=@path WITH COPY_ONLY, INIT, CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;";
        backup.Parameters.AddWithValue("@path", backupPath); await backup.ExecuteNonQueryAsync();
        backup.CommandText = "RESTORE FILELISTONLY FROM DISK=@path";
        var files = new List<(string Logical, string Type)>();
        await using (var reader = await backup.ExecuteReaderAsync())
            while (await reader.ReadAsync()) files.Add((reader.GetString(0), reader.GetString(2)));
        backup.Parameters.Clear();
        backup.CommandText = "SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)),CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000))";
        string dataDirectory, logDirectory;
        await using (var reader = await backup.ExecuteReaderAsync()) { await reader.ReadAsync(); dataDirectory = reader.GetString(0); logDirectory = reader.GetString(1); }
        var moves = new List<string>();
        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var dir = file.Type == "L" ? logDirectory : dataDirectory;
            var destination = dir.TrimEnd('\\', '/') + (dir.Contains('\\') ? "\\" : "/") + restoredName + "_" + i + (file.Type == "L" ? ".ldf" : ".mdf");
            backup.Parameters.AddWithValue("@logical" + i, file.Logical); backup.Parameters.AddWithValue("@file" + i, destination);
            moves.Add($"MOVE @logical{i} TO @file{i}");
        }
        backup.Parameters.AddWithValue("@path", backupPath);
        backup.CommandText = $"RESTORE DATABASE [{restoredName}] FROM DISK=@path WITH CHECKSUM, RECOVERY, " + string.Join(", ", moves) + $"; DBCC CHECKDB([{restoredName}]) WITH NO_INFOMSGS;";
        await backup.ExecuteNonQueryAsync();
    }
    var finalCounts = await Counts(source.ConnectionString);
    var restoredConnection = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = restoredName };
    var restoredCounts = await Counts(restoredConnection.ConnectionString);
    if (!finalCounts.OrderBy(x => x.Key).SequenceEqual(restoredCounts.OrderBy(x => x.Key)))
        throw new InvalidOperationException("Restored table counts do not match the source database.");
    await using (var restored = new StockFlowDbContext(new DbContextOptionsBuilder<StockFlowDbContext>().UseSqlServer(restoredConnection.ConnectionString).Options))
    {
        var mismatches = await restored.InventoryBalances.Where(b => b.OnHand != (restored.StockMovements.Where(m => m.StockLotId == b.StockLotId).Sum(m => (decimal?)m.OnHandDelta) ?? 0)
            || b.Reserved != (restored.StockMovements.Where(m => m.StockLotId == b.StockLotId).Sum(m => (decimal?)m.ReservedDelta) ?? 0)
            || b.Reserved != (restored.StockReservations.Where(r => r.StockLotId == b.StockLotId && r.Status == ReservationStatus.Active).Sum(r => (decimal?)r.Quantity) ?? 0)).CountAsync();
        if (mismatches != 0) throw new InvalidOperationException("Restored stock does not reconcile.");
    }
    restoreTimer.Stop();
    var readP95 = P95(readings); var postP95 = P95(postings);
    var unexpected = errors.Where(x => x.Key == 0 || x.Key >= 500).Sum(x => x.Value);
    var result = new
    {
        startedAtUtc = runStarted, completedAtUtc = DateTime.UtcNow, deployment = new
        {
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            host = "Local Kestrel and SQL Server on the same machine", concurrentUsers = 30, thinkTimeMilliseconds = 250
        },
        initialCounts = initial, finalCounts, workloadSeconds = workload.Elapsed.TotalSeconds,
        requests = total, reads = readings.Count, postings = postings.Count, readP95Milliseconds = readP95,
        postingP95Milliseconds = postP95, errors, unexpectedErrorRate = (double)unexpected / total,
        oneLineDraftP95Milliseconds = P95(oneLinePostings), twentyLineDraftP95Milliseconds = P95(twentyLinePostings),
        loadTargetMet = seconds >= 900 && readP95 <= 1000 && postP95 <= 2000 && (double)unexpected / total < 0.01 && errors.IsEmpty,
        workload = "80% paginated lots/orders/product-stock reads; 20% draft sales order commands alternating 1 and 20 lines; 250ms think time per user",
        restore = new { verifiedBackupChecksum = true, dbccCheckDbPassed = true, tableCountsMatch = true,
            inventoryReconciled = true, elapsedSeconds = restoreTimer.Elapsed.TotalSeconds, backupPath,
            destination = "Fresh database and data/log files on the same SQL Server instance; separate-host disaster recovery is not measured" }
    };
    await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Results: {outputPath}; reads p95 {readP95:F1}ms; postings p95 {postP95:F1}ms; requests {total}; errors {errors.Values.Sum()}");
    if (!result.loadTargetMet) Environment.ExitCode = 1;
}
finally
{
    if (server is { HasExited: false }) { server.Kill(entireProcessTree: true); await server.WaitForExitAsync(); }
    server?.Dispose(); SqlConnection.ClearAllPools();
    await File.WriteAllLinesAsync(Path.ChangeExtension(outputPath, ".api.log"), apiLog);
    await using var connection = new SqlConnection(master.ConnectionString); await connection.OpenAsync();
    foreach (var name in new[] { databaseName, restoredName })
    {
        var prefix = name.StartsWith("StockFlow_Operations_", StringComparison.Ordinal) ? "StockFlow_Operations_" : "StockFlow_Restore_";
        if (!Guid.TryParseExact(name[prefix.Length..], "N", out _)) throw new InvalidOperationException("Unexpected cleanup database name.");
        await using var drop = connection.CreateCommand(); drop.CommandTimeout = 120;
        drop.CommandText = $"IF DB_ID(@name) IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
        drop.Parameters.AddWithValue("@name", name); await drop.ExecuteNonQueryAsync();
    }
}

static async Task Csrf(HttpClient client)
{
    var response = await client.GetFromJsonAsync<JsonElement>("/api/auth/csrf");
    client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", response.GetProperty("token").GetString());
}
static double P95(IEnumerable<double> samples)
{
    var sorted = samples.Order().ToArray(); return sorted.Length == 0 ? 0 : sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1];
}
static async Task<Dictionary<string, long>> Counts(string connectionString)
{
    await using var connection = new SqlConnection(connectionString); await connection.OpenAsync();
    await using var tables = connection.CreateCommand(); tables.CommandText = "SELECT name FROM sys.tables WHERE is_ms_shipped=0 AND name <> '__EFMigrationsHistory' ORDER BY name";
    var names = new List<string>(); await using (var reader = await tables.ExecuteReaderAsync()) while (await reader.ReadAsync()) names.Add(reader.GetString(0));
    var result = new Dictionary<string, long>();
    foreach (var name in names)
    {
        await using var count = connection.CreateCommand(); count.CommandText = $"SELECT COUNT_BIG(*) FROM [{name.Replace("]", "]]", StringComparison.Ordinal)}]";
        result.Add(name, (long)(await count.ExecuteScalarAsync())!);
    }
    return result;
}
