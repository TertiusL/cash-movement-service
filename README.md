# Cash Movements Service

This .NET 8 gRPC service records cash movements, retrieves current balances and streams account statements from SQL Server.

## Build on Windows

Open `CashMovements.sln` in Rider or run the following from the repository root in PowerShell. Use .NET SDK 8.0.428 (pinned by `global.json`) and Docker Desktop with Linux containers. The first restore downloads the pinned NuGet dependencies and the local EF tool. SQL Server runs in Docker.

```powershell
dotnet --version
dotnet tool restore
dotnet restore CashMovements.sln
dotnet build CashMovements.sln --no-restore -c Release -m:1
```

| Project | Responsibility at this checkpoint |
|---|---|
| Domain | Identifier, timestamp, narration and money rules. Defines equality of canonical movements. |
| UseCases | Validates inputs and exposes typed submission, balance and statement results. |
| Infrastructure | EF Core mappings, atomic recording and duplicate recovery, SQL aggregates and snapshot-based statement reads. |
| Grpc | HTTP/2 host, protobuf mapping, incremental statement writes, typed error trailers and correlated outcome logs. |
| UnitTests / IntegrationTests | Domain boundary cases and generated-client gRPC calls against a migrated SQL Server database. |

## Start and initialise Docker SQL Server

Choose a strong local password and keep it available for subsequent sessions using this volume. Set it in the current PowerShell process at the prompt. Compose uses the environment variable for local database setup. The default host binding is `127.0.0.1:14333`; set `CASHMOVEMENTS_SQL_PORT` before startup if you need another port.

```powershell
$env:MSSQL_SA_PASSWORD = [System.Net.NetworkCredential]::new('', (Read-Host 'Local SQL Server password' -AsSecureString)).Password
docker compose up -d --wait db
docker compose run --rm db-init
```

The [Compose configuration](compose.yaml) runs SQL Server 2022 Developer and uses a named data volume. The one-shot initializer executes [bootstrap](db/bootstrap.sql), the [EF-generated schema](db/schema.sql) and [base seed](db/seed.sql) in that order. Bootstrap enables snapshot isolation on the `CashMovements` database. Rerun `docker compose run --rm db-init` to apply any missing migrations and seed keys. The standard seed includes `ACC-001` with a ZAR deposit and withdrawal totalling R12,249.50, plus empty `ACC-ZAR`, `ACC-JPY` and `ACC-BHD` accounts.

The separate `docker compose run --rm db-init large-demo` fixture adds `ACC-DEMO`, a June 2024 opening deposit of R100.00 and 50,001 July movements. July closes at R100.50.

## Run and test submission and balance

In the same PowerShell session, build the connection string from the local password. The builder quotes password characters such as semicolons correctly. Use the same host port as Compose.

```powershell
$sqlPort = if ($env:CASHMOVEMENTS_SQL_PORT) { $env:CASHMOVEMENTS_SQL_PORT } else { '14333' }
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection.psbase.ConnectionString = "Server=tcp:127.0.0.1,$sqlPort;Database=CashMovements;User ID=sa;Encrypt=True;TrustServerCertificate=True"
$connection['Password'] = $env:MSSQL_SA_PASSWORD
$env:ConnectionStrings__CashMovements = $connection.psbase.ConnectionString
$env:CASHMOVEMENTS_TEST_CONNECTION = $env:ConnectionStrings__CashMovements
dotnet test CashMovements.sln --no-build -c Release
dotnet run --no-build -c Release --project src/CashMovements.Grpc --launch-profile CashMovements.Grpc
```

The tests start their own HTTP/2 hosts and create, migrate and remove uniquely named test databases on the supplied SQL Server instance. Use a local Docker login with database creation rights. To run only the fast tests, use `dotnet test tests/CashMovements.UnitTests -c Release`.

The Rider launch profile listens at `https://localhost:5001`. Supply `ConnectionStrings__CashMovements` in the host process environment when launching from Rider. If Kestrel needs a development certificate, run `dotnet dev-certs https`.

To try both RPCs from a second PowerShell session, use the following commands from the repository root if you have `grpcurl`. The generated-client tests above exercise both calls without requiring a separate client tool. The `-insecure` option is for this loopback development certificate.

```powershell
$proto = 'src/CashMovements.Grpc/Protos'
$file = 'cashmovements/v1/cash_movements.proto'
'{"movement":{"accountId":"acc-zar","externalRef":"review/001","currency":"zar","amount":"125.5000","occurredAt":"2024-07-15T22:30:00Z","narration":"Review deposit"}}' | grpcurl -insecure -import-path $proto -proto $file -d '@' localhost:5001 cashmovements.v1.CashMovements/SubmitMovement
'{"accountId":"acc-zar"}' | grpcurl -insecure -import-path $proto -proto $file -d '@' localhost:5001 cashmovements.v1.CashMovements/GetBalance
```

On a fresh base seed, the first call returns `SUBMISSION_DISPOSITION_RECORDED` with `ACC-ZAR`, `REVIEW/001`, `ZAR` and `125.50`. Repeating it yields `SUBMISSION_DISPOSITION_ALREADY_RECORDED` and the balance stays `125.50`. Changing just the narration on that key returns `ALREADY_EXISTS` with reason `CONFLICT` in the protobuf `cash-error-bin` trailer. The server returns a `trace-id` trailer for correlation. See the [contract](src/CashMovements.Grpc/Protos/cashmovements/v1/cash_movements.proto) and [end-to-end examples](tests/CashMovements.IntegrationTests/MovementRpcTests.cs).

## Stream a statement

With the base seed loaded and the host running, use `grpcurl` from a second PowerShell session in the repository root to request just 16 July for `ACC-001`.

```powershell
'{"accountId":"acc-001","fromInclusive":"2024-07-16T00:00:00Z","toExclusive":"2024-07-17T00:00:00Z"}' | grpcurl -insecure -import-path src/CashMovements.Grpc/Protos -proto cashmovements/v1/cash_movements.proto -d '@' localhost:5001 cashmovements.v1.CashMovements/ExportStatement
```

On the base seed, the header gives the canonical account, currency, requested UTC bounds and an opening balance of `12500.00`. The next frame carries the `-250.50` withdrawal and a running balance of `12249.50`. Successful end-of-stream confirms that closing balance. A range with no movements emits just the header and closes at its opening balance. A non-OK terminal status indicates an incomplete export even when the client has received rows.

To run only the statement checks after building and setting `CASHMOVEMENTS_TEST_CONNECTION`, use the following command. The generated client reads incrementally over loopback HTTP/2. The tests load the large fixture into a disposable database.

```powershell
dotnet test tests/CashMovements.IntegrationTests --no-build -c Release --filter 'FullyQualifiedName~Statement'
```

## Inspect the database

In Rider's Database window, add a Microsoft SQL Server data source for host `127.0.0.1`, port `14333`, database `CashMovements`, user `sa` and the local password. Trust the local container certificate for this development connection. Expand `dbo` to see the tables, composite currency foreign key and the unique index on `(AccountId, ExternalRef)`.

Alternatively, run the inspection query with the container's own SQL client from the same PowerShell session:

```powershell
$env:SQLCMDPASSWORD = $env:MSSQL_SA_PASSWORD
docker compose exec -T -e SQLCMDPASSWORD db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d CashMovements -Q "SELECT a.AccountId, a.Currency, COUNT(m.MovementId) AS Movements, COALESCE(SUM(m.Amount),0) AS Balance FROM dbo.Accounts AS a LEFT JOIN dbo.Movements AS m ON m.AccountId=a.AccountId GROUP BY a.AccountId,a.Currency ORDER BY a.AccountId"
docker compose exec -T -e SQLCMDPASSWORD db /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -C -b -d CashMovements -Q "SELECT * FROM dbo.__EFMigrationsHistory"
```

Stop the local environment with `docker compose down`. The named volume retains your data.

## Verify or regenerate the schema

After a Release build and `dotnet tool restore`, run the isolated database setup check:

```powershell
.\tools\Verify-DatabaseSetup.ps1
```

The check uses its own Docker Compose project, port 14433 and a disposable database volume. Use `-Port` to choose a different free port. It compares the scripted schema with the EF migrations, upgrades the original schema with an existing posting, reruns both seed modes and removes its disposable resources on exit. [Foundation verification](PLANNING.md#foundation-verification) · [Statement and upgrade verification](PLANNING.md#step-4-verification)

EF migrations in Infrastructure are the schema source of truth. From the root, regenerate the committed SQL after an intentional migration change:

```powershell
dotnet ef migrations script --idempotent --project src/CashMovements.Infrastructure --startup-project src/CashMovements.Infrastructure --output db/schema.sql
```

Design-time script generation works offline once dependencies are restored. For an explicit EF database update, supply a connection string to the Docker instance via `ConnectionStrings__CashMovements`. For normal local setup use the Compose initializer above.

[Approved implementation plan](IMPLEMENTATION_PLAN.md) · [Design decisions and trade-offs](SOLUTION.md) · [AI assistance and verification history](PLANNING.md)
