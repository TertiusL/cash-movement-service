# Cash Movements Service

This checkpoint contains the .NET 8 solution, the versioned gRPC contract and a runnable SQL Server schema with repeatable seeds. The three RPCs are defined in protobuf. Business handlers, business tests, streaming execution and the console CSV client belong to later stages.

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
| Domain | Inner project reserved for business rules. |
| UseCases | References Domain and will own transport-independent operations and outcomes. |
| Infrastructure | EF Core SQL Server mappings, design-time context, migration and model snapshot. |
| Grpc | ASP.NET Core host and generated server types for the versioned contract. |
| UnitTests / IntegrationTests | Test project scaffolding. The executable database setup check is described below. |

Inspect [the contract](src/CashMovements.Grpc/Protos/cashmovements/v1/cash_movements.proto) and [the EF model](src/CashMovements.Infrastructure/Persistence/CashMovementsDbContext.cs). To start the host shell after the Release build, run `dotnet run --no-build -c Release --project src/CashMovements.Grpc --launch-profile CashMovements.Grpc` or use its Rider launch profile. The HTTPS endpoint is `https://localhost:5001`. If Kestrel reports a missing development certificate, check with `dotnet dev-certs https --check` and create one with `dotnet dev-certs https`. RPC handlers will be connected in steps 3 and 4.

## Start and initialise Docker SQL Server

Choose a strong local password and keep it available for subsequent sessions using this volume. Set it in the current PowerShell process at the prompt. Compose uses the environment variable for local database setup. The default host binding is `127.0.0.1:14333`; set `CASHMOVEMENTS_SQL_PORT` before startup if you need another port.

```powershell
$env:MSSQL_SA_PASSWORD = [System.Net.NetworkCredential]::new('', (Read-Host 'Local SQL Server password' -AsSecureString)).Password
docker compose up -d --wait db
docker compose run --rm db-init
```

The [Compose configuration](compose.yaml) runs SQL Server 2022 Developer and uses a named data volume. The one-shot initializer executes [bootstrap](db/bootstrap.sql), the [EF-generated schema](db/schema.sql) and [base seed](db/seed.sql) in that order. Bootstrap enables snapshot isolation on the `CashMovements` database. Rerun `docker compose run --rm db-init` to apply any missing migrations and seed keys. The standard seed includes `ACC-001` with a ZAR deposit and withdrawal totalling R12,249.50, plus empty `ACC-ZAR`, `ACC-JPY` and `ACC-BHD` accounts.

The separate `docker compose run --rm db-init large-demo` fixture adds `ACC-DEMO`, a June 2024 opening deposit of R100.00 and 50,001 July movements. July closes at R100.50. This prepares data for the later statement stage.

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

The check uses its own Docker Compose project, port 14433 and a disposable database volume. Use `-Port` to choose a different free port. It compares the scripted schema with an EF migration applied independently, reruns setup with existing data and removes its disposable resources on exit. See [the verification record](PLANNING.md#foundation-verification) for the checks and results from this checkpoint.

EF migrations in Infrastructure are the schema source of truth. From the root, regenerate the committed SQL after an intentional migration change:

```powershell
dotnet ef migrations script --idempotent --project src/CashMovements.Infrastructure --startup-project src/CashMovements.Infrastructure --output db/schema.sql
```

Design-time script generation works offline once dependencies are restored. For an explicit EF database update, supply a connection string to the Docker instance via `ConnectionStrings__CashMovements`. For normal local setup use the Compose initializer above.

[Approved implementation plan](IMPLEMENTATION_PLAN.md) · [Design decisions and trade-offs](SOLUTION.md) · [AI assistance and verification history](PLANNING.md)
