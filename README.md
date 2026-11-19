# Cash Movements Service

**Assessment video.** The recording is in the repository root. Download [Assessment.mp4](Assessment.mp4) from GitHub or open it directly from your clone.

A .NET 8 gRPC service for recording cash movements, checking account balances and streaming statements. SQL Server runs in Docker. The API runs in your IDE.

## 1. Clone and configure

Clone [the repository](https://github.com/TertiusL/cash-movement-service) using your IDE or Git client. Install the pinned **.NET SDK 8.0.428** if needed, and start **Docker Desktop** in Linux containers mode.

Create these two local files in the checkout. Replace `YOUR_PASSWORD` in both with the **same** local SQL Server password. For a new database we recommend a random password of 16 or more characters using uppercase letters, lowercase letters and digits. If you already have a database volume, use its existing password. Both files are ignored by Git. [SQL Server password requirements](https://learn.microsoft.com/en-us/sql/linux/sql-server-linux-configure-environment-variables#use-with-initial-setup)

**`.env`** — next to `CashMovements.sln`, for Docker Compose

```dotenv
MSSQL_SA_PASSWORD=YOUR_PASSWORD
```

**`src/CashMovements.Grpc/appsettings.Development.json`** — in the API project, for the IDE's Debug launch

```json
{
  "ConnectionStrings": {
    "CashMovements": "Server=tcp:127.0.0.1,14333;Database=CashMovements;User ID=sa;Password=YOUR_PASSWORD;Encrypt=True;TrustServerCertificate=True"
  }
}
```

## 2. Start and seed SQL Server

Run this from the repository root. If you previously set `$env:MSSQL_SA_PASSWORD` in this PowerShell terminal, first run `Remove-Item Env:MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue` so Compose reads the password from `.env`. Continue to Debug once the seed command exits successfully.

```shell
docker compose run --rm db-init
```

The command starts SQL Server, creates the database and schema, and seeds both statement examples. On a fresh database, `ACC-001` holds **12,249.50 ZAR** across two movements. The busy account, `ACC-DEMO`, has a June opening deposit and **50,001 movements in July 2024**. The three accounts for trying submissions (`ACC-ZAR`, `ACC-JPY` and `ACC-BHD`) start at zero.

If you ran an earlier version of setup, rerun the same command to add the busy account. Existing movements are preserved.

To run automated tests now, go to [test database setup](#tests-and-design-notes). The integration tests start their own API.

### Optional clean reset of the local database

**This permanently deletes all data in this Compose project's SQL Server volume, including movements you submitted.** For a clean setup, stop the API debugger and check that the passwords in `.env` and `src/CashMovements.Grpc/appsettings.Development.json` match.

Run these commands from the repository root in the same PowerShell terminal. They clear the terminal's password override, remove the project's containers and data volume, then create and seed a fresh database using `.env`.

```powershell
Remove-Item Env:MSSQL_SA_PASSWORD -ErrorAction SilentlyContinue
docker compose down --volumes
docker compose run --rm db-init
```

Once seeding succeeds, start Debug again. The `GetBalance` example in step 4 should return `12249.50` ZAR for `ACC-001`.


## 3. Debug the API

Open [CashMovements.sln](CashMovements.sln) in your .NET IDE. Select **CashMovements.Grpc** as the startup project, choose its **CashMovements.Grpc** launch profile and start **Debug**. The IDE restores packages and builds the project.

Look for **`Now listening on: https://localhost:5001`** in the run output. This is the gRPC address for all three methods. If the local HTTPS development certificate needs to be trusted, run `dotnet dev-certs https --trust` once and accept the prompt.


## 4. Try a request

Use any gRPC client with the [protobuf contract](src/CashMovements.Grpc/Protos/cashmovements/v1/cash_movements.proto). To try it in **Postman Desktop**, follow these steps.

1. Select **New → gRPC**, enter `localhost:5001` and enable **TLS** (the lock icon).
2. Under **Service definition**, choose **Import a .proto file** and open `src/CashMovements.Grpc/Protos/cashmovements/v1/cash_movements.proto`.
3. Choose a method under **CashMovements**, paste its JSON into **Message** and click **Invoke**. For a balance check, choose **GetBalance** and use the message below. You can also go straight to either [statement example](#stream-a-statement).

```json
{"account_id":"ACC-001"}
```

On the fresh seed, expect account `ACC-001`, currency `ZAR` and balance `"12249.50"`. [Postman gRPC guide](https://learning.postman.com/docs/use/send-requests/protocols/grpc/using-service-definition)

### Try SubmitMovement

With an unused reference on `ACC-ZAR`, send this twice. The first call records the movement and the second returns `SUBMISSION_DISPOSITION_ALREADY_RECORDED`. The balance increases only once. Change the narration and resend to see a conflict (`ALREADY_EXISTS`).

```json
{"movement":{"account_id":"ACC-ZAR","external_ref":"REVIEW/001","currency":"ZAR","amount":"125.50","occurred_at":"2024-07-15T22:30:00Z","narration":"Review deposit"}}
```


### Stream a statement

Select **ExportStatement** in Postman and invoke either request first. Both use accounts from the initial seed.

**Small account · 16 July 2024.** Expect a header with opening balance `12500.00`, followed by one withdrawal of `-250.50`. The closing balance is `12249.50`.

```json
{"account_id":"ACC-001","from_inclusive":"2024-07-16T00:00:00Z","to_exclusive":"2024-07-17T00:00:00Z"}
```

**Busy account · first two minutes of July.** Expect a header with opening balance `100.00`, then 120 movements alternating between `-1.00` and `1.00`. The last running balance is `100.00`.

```json
{"account_id":"ACC-DEMO","from_inclusive":"2024-07-01T00:00:00Z","to_exclusive":"2024-07-01T00:02:00Z"}
```

## Export a statement to CSV

### Optional console client

For the **full July stream**, use the console client. Postman displays each message in a timeline and can run out of memory displaying all 50,001 movements. The console client writes each movement to disk as it arrives. You can run it immediately after setup, before or after the Postman examples.

Keep the API running and run this command from the repository root. It writes `july.csv` to the included [exports folder](exports/) next to the solution. Git ignores generated exports.

```shell
dotnet run --project src/CashMovements.StatementClient -- --endpoint https://localhost:5001 --account ACC-DEMO --from 2024-07-01T00:00:00Z --to 2024-08-01T00:00:00Z --output exports/july.csv
```

Watch the progress count and open **`exports/july.csv`** after success. July opens at `100.00`, has 50,001 movement records and closes at `100.50`. Ctrl+C cancels the export. An interrupted write leaves a file ending in `.partial`. Use a new filename for each completed export.

Client exit codes are **0** for a published CSV, **1** for export failure, **2** for invalid arguments and **130** for cancellation.


## Tests and design notes

`CashMovements.UnitTests` runs without Docker or a connection string. For `CashMovements.IntegrationTests`, complete steps 1 and 2 and leave Docker SQL Server running. The test runner is a separate process. It reads **`CASHMOVEMENTS_TEST_CONNECTION`** from its own environment.

### Run the tests from PowerShell

Open PowerShell in the repository root, either in a standalone window or in your IDE's terminal. Run all three lines in the **same session**. They read the connection string from step 1, pass it to the test process and run the full solution.

```powershell
$settings = Get-Content -Raw -LiteralPath .\src\CashMovements.Grpc\appsettings.Development.json | ConvertFrom-Json
$env:CASHMOVEMENTS_TEST_CONNECTION = $settings.ConnectionStrings.CashMovements
dotnet test CashMovements.sln -c Release
```

### Run the tests from an IDE

1. In your IDE's **test run configuration**, add an environment variable named `CASHMOVEMENTS_TEST_CONNECTION`. The API's Debug profile and the test runner have separate environments.
2. For its value, open your local `src/CashMovements.Grpc/appsettings.Development.json` and copy the entire `ConnectionStrings` → `CashMovements` value, including the SQL port, password and semicolons. Omit the surrounding JSON quotes and save the setting locally.
3. Start a new test run for `CashMovements.IntegrationTests` or the full solution. If your IDE has no test environment editor, use the PowerShell commands above.

**What happens to the database?** The fixture connects to `master`, creates a uniquely named `CashMovements_Test_...` database, applies migrations and drops that database during cleanup. The local Docker `sa` login from step 1 has the required permissions. Your seeded `CashMovements` data stays available for manual testing.

**If setup fails**

- `Set CASHMOVEMENTS_TEST_CONNECTION` means the test process did not receive the variable. Set it in the IDE's test run configuration if you launch tests there, or in the same terminal where you run `dotnet test`.
- `Login failed for user 'sa'` means SQL Server rejected the login. Check that the test connection string uses the working password for this Docker volume and the host port shown by `docker compose port db 1433`.

[Design decisions and trade-offs](SOLUTION.md) · [Requirements and verification](IMPLEMENTATION_PLAN.md) · [AI assistance record](PLANNING.md#ai-assistance-record) · [Narrated demo script](DEMO_SCRIPT.md)
