# StockFlow — Phases 1–5

ASP.NET Core 10 API and SQL Server foundation for the manufacturing specification in [docs](CSharp2/docs/StockFlow-Manufacturing-Specification.md).

Phase 1 includes departments, employee profiles and lifecycle, optional employee logins, scoped roles, customers/suppliers/products, shared lot inventory, audit history, and durable command replay. Phase 2 adds purchase-order approval, partial/full receiving, and incoming QC. Phase 3 adds sales demand, approved BOM revisions, work orders, full material issue, final output/scrap, finished-goods QC, and lot genealogy. Phase 4 adds whole-order reservations, shipment preparation, dispatch, delivery, and pre-issue cancellation. Phase 5 adds source invoices, external payment tracking, scoped dashboards, reconciliation, and load/restore verification. The original console project is now the API; there is no browser UI yet.

All five phases are implemented with versioned migrations. Full verification uses disposable SQL Server databases; it does not migrate the existing local application database. Apply the migrations below before using the new APIs there. See the [phase 4 guide](CSharp2/docs/Phase-4-Implementation.md), [phase 5 guide](CSharp2/docs/Phase-5-Implementation.md), and [verification results](CSharp2/docs/Phase-5-Verification.md).

## Run locally

Requires .NET 10 SDK and a SQL Server instance. The default connection uses Windows authentication against `localhost`, existing database `newDB`. SQL Server must allow the current Windows user to access this database and apply schema migrations; use an application-specific login/connection for a deployed environment.

The local `newDB` setup is complete, and the administrator email is `admin@stock.com`. Saved local login details are in [the local access note](CSharp2/.local/Local-Access.md), which Git ignores.

Run these commands from this solution directory in PowerShell 7:

```powershell
dotnet restore
$env:ASPNETCORE_ENVIRONMENT = 'Development'
# Optional: override the local SQL Server connection.
# $env:ConnectionStrings__StockFlow = 'Server=...;Database=newDB;...'
dotnet run --project .\CSharp2 -- --migrate
dotnet run --project .\CSharp2 -- --urls http://localhost:5080
```

`--migrate` applies versioned migrations, seeds the organization/access data, and exits. Bootstrap creates the first login only when no login exists; it never resets an existing administrator. There are no default credentials. Repeating migration/seeding preserves department changes and adds no duplicate sample records. The nine synthetic employee profiles have no logins or implicit grants.

For a fresh database with no login, create the initial administrator before starting the API:

```powershell
$env:Bootstrap__Email = 'admin@stock.com'
$env:Bootstrap__Password = Read-Host 'Choose the initial admin password (12–128 characters)' -MaskInput
dotnet run --project .\CSharp2 -- --migrate
Remove-Item Env:Bootstrap__Password, Env:Bootstrap__Email
```

`GET /health/live` checks the process; `GET /health/ready` checks that the application schema can be queried. Normal startup does not migrate automatically. `Database__MigrateOnStartup=true` is available for disposable local environments; use the explicit migration step for deployments.

In production, use HTTPS, a trusted SQL Server certificate, an external connection secret, and a durable protected data-protection key directory (`CSharp2/.local/keys` by default). The development connection trusts the local SQL certificate. Cookie and CSRF tokens require HTTPS outside Development. Do not commit `.local` or credentials.

## Call the API

Authentication uses an HttpOnly cookie. Every write, including login/logout, requires the CSRF cookie and `X-CSRF-TOKEN` header. Refresh the token after login because it is bound to the authenticated identity.

```powershell
$base = 'http://localhost:5080'
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$csrf = Invoke-RestMethod "$base/api/auth/csrf" -WebSession $session
$credentials = @{ email = 'admin@stock.com'; password = (Read-Host 'Password' -MaskInput) } | ConvertTo-Json
Invoke-RestMethod "$base/api/auth/login" -Method Post -WebSession $session -ContentType 'application/json' -Headers @{ 'X-CSRF-TOKEN' = $csrf.token } -Body $credentials
$csrf = Invoke-RestMethod "$base/api/auth/csrf" -WebSession $session
$headers = @{ 'X-CSRF-TOKEN' = $csrf.token }
Invoke-RestMethod "$base/api/departments" -WebSession $session

$product = @{
    requestId = [guid]::NewGuid().ToString()
    sku = 'RM-COTTON'; name = 'Cotton'; itemType = 'RawMaterial'
    baseUnit = 'kg'; sellingPrice = 0; reorderLevel = 100; isActive = $true
} | ConvertTo-Json
Invoke-RestMethod "$base/api/products" -Method Post -WebSession $session -Headers $headers -ContentType 'application/json' -Body $product
```

Keep `requestId` and the original payload when retrying a business command. The same user, command type, ID, and payload return the original status/body. Reusing the ID with a changed target or payload returns `409`. Current permission is required even for a replay. Reads and writes denied for a specific object consistently return `404`; denied collection or company operations return `403`.

See [the phase 1 guide](CSharp2/docs/Phase-1-Implementation.md) for foundation routes and scope rules, [the phase 2 guide](CSharp2/docs/Phase-2-Implementation.md) for purchasing and receiving, [the phase 3 guide](CSharp2/docs/Phase-3-Implementation.md) for manufacturing, [the phase 4 guide](CSharp2/docs/Phase-4-Implementation.md) for fulfilment, and [the phase 5 guide](CSharp2/docs/Phase-5-Implementation.md) for finance and reports. Operational roles and cross-department read grants must be explicitly assigned.

## Verify

Run the complete regression suite against SQL Server:

```powershell
dotnet build .\Csharp2.sln --configuration Release
# Optional for SQL authentication or a non-default instance:
# $env:STOCKFLOW_TEST_CONNECTION = 'Server=...;User ID=...;Password=...;Encrypt=true;...'
dotnet test .\Csharp2.sln --configuration Release --no-build
```

Tests use the real SQL Server provider, HTTP cookie login, and CSRF protection. Each run creates a unique `StockFlow_Phase1_Test_<guid>` database and drops only that database afterward. The test SQL principal needs database creation/deletion permissions. Tests never migrate or clear the configured application's existing database. A missing database server fails the suite rather than silently skipping it.

The GitHub Actions workflow builds all projects and runs the complete suite against a disposable SQL Server container. The suite includes the complete nine-department manufactured-order scenario, existing-stock fulfilment, offboarding, a 100-request final-unit race, replay, rollback, and receipt/payment limits. See [full verification results](CSharp2/docs/Phase-5-Verification.md). The separate [operations runner](CSharp2/docs/Phase-5-Operations.md) reproduces the full-scale load benchmark and backup/restore drill.

To generate future migrations, run `dotnet tool restore`, then `dotnet ef migrations add <Name> --project .\CSharp2`. The checked-in tool manifest pins EF to the application version.
