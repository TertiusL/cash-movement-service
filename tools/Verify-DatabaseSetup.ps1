[CmdletBinding()]
param([ValidateRange(1024, 65535)][int]$Port = 14433)

# Prerequisites: docker compose, dotnet tool restore, dotnet build -c Release.
$ErrorActionPreference = 'Stop'
$project = 'cashverify-' + [guid]::NewGuid().ToString('N')
$volume = "${project}_sql-data"
$network = "${project}_default"
$ownsProject = $false
$envNames = 'MSSQL_SA_PASSWORD', 'SQLCMDPASSWORD', 'CASHMOVEMENTS_SQL_PORT', 'ConnectionStrings__CashMovements'
$oldEnvironment = @{}
foreach ($envName in $envNames) {
    $oldEnvironment[$envName] = [Environment]::GetEnvironmentVariable($envName, 'Process')
}

function Invoke-Tool([string]$Tool, [string[]]$Arguments) {
    & $Tool @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with code $LASTEXITCODE." }
}

function Invoke-Compose([string[]]$Arguments) {
    Invoke-Tool docker (@('compose', '-p', $project, '-f', 'compose.yaml') + $Arguments)
}

function Invoke-Database([string]$Query, [string]$Database = 'CashMovements') {
    $sqlArgs = 'exec', '-i', '--env', 'SQLCMDPASSWORD', $dbContainer, '/opt/mssql-tools18/bin/sqlcmd',
               '-S', 'localhost', '-U', 'sa', '-C', '-b', '-r1', '-h', '-1', '-W', '-d', $Database
    "SET NOCOUNT ON;`n$Query`nGO" | & docker @sqlArgs
    if ($LASTEXITCODE -ne 0) { throw "SQL verification failed in $Database (exit $LASTEXITCODE)." }
}

# Hash all columns in key order, including identities and migration history.
function Get-DataSignature {
    $query = @'
SELECT CONVERT(char(64), HASHBYTES('SHA2_256',
    (SELECT * FROM dbo.Accounts ORDER BY AccountId FOR JSON PATH, INCLUDE_NULL_VALUES)), 2);
SELECT CONVERT(char(64), HASHBYTES('SHA2_256',
    (SELECT * FROM dbo.Movements ORDER BY MovementId FOR JSON PATH, INCLUDE_NULL_VALUES)), 2);
SELECT CONVERT(char(64), HASHBYTES('SHA2_256',
    (SELECT * FROM dbo.__EFMigrationsHistory ORDER BY MigrationId FOR JSON PATH)), 2);
'@
    (Invoke-Database $query) -join "`n"
}

function Test-Rerun([string]$Mode) {
    $before = Get-DataSignature
    Invoke-Compose @('run', '--rm', 'db-init', $Mode) | Out-Null
    if ((Get-DataSignature) -cne $before) { throw "Data or migration history changed during the $Mode rerun." }
    Write-Host "PASS: $Mode rerun preserved every account, movement and migration history row."
}

function Get-SchemaSignature([string]$Database) {
    # Compare logical metadata. Object IDs and index allocation can differ between databases.
    $query = @'
SELECT CONVERT(char(64), HASHBYTES('SHA2_256', (
    SELECT SCHEMA_NAME(t.schema_id) AS [schema], t.name AS tbl, c.column_id, c.name, TYPE_NAME(c.user_type_id) AS type,
           c.max_length, c.precision, c.scale, c.is_nullable, c.collation_name, c.is_identity
    FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id
    WHERE t.is_ms_shipped=0 ORDER BY [schema], tbl, c.column_id FOR JSON PATH)), 2);
SELECT CONVERT(char(64), HASHBYTES('SHA2_256', (
    SELECT OBJECT_NAME(i.object_id) AS tbl, i.name, i.type_desc, i.is_unique, i.is_primary_key,
           i.is_unique_constraint, ic.key_ordinal, ic.is_included_column, ic.is_descending_key, c.name AS col
    FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id
    JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id
    JOIN sys.columns c ON c.object_id=i.object_id AND c.column_id=ic.column_id
    WHERE t.is_ms_shipped=0 ORDER BY tbl, i.name, ic.index_column_id FOR JSON PATH)), 2);
SELECT CONVERT(char(64), HASHBYTES('SHA2_256', (
    SELECT f.name, OBJECT_NAME(fc.parent_object_id) AS tbl, COL_NAME(fc.parent_object_id, fc.parent_column_id) AS col,
           OBJECT_NAME(fc.referenced_object_id) AS targetTable, COL_NAME(fc.referenced_object_id, fc.referenced_column_id) AS targetCol,
           fc.constraint_column_id, f.delete_referential_action, f.update_referential_action, f.is_disabled, f.is_not_trusted
    FROM sys.foreign_keys f JOIN sys.foreign_key_columns fc ON f.object_id=fc.constraint_object_id
    ORDER BY f.name, fc.constraint_column_id FOR JSON PATH)), 2);
SELECT CONVERT(char(64), HASHBYTES('SHA2_256', (
    SELECT OBJECT_NAME(parent_object_id) AS tbl, name, definition, is_disabled, is_not_trusted
    FROM sys.check_constraints ORDER BY tbl, name FOR JSON PATH)), 2);
SELECT CONVERT(char(64), HASHBYTES('SHA2_256', (
    SELECT * FROM dbo.__EFMigrationsHistory ORDER BY MigrationId FOR JSON PATH)), 2);
'@
    (Invoke-Database $query $Database) -join "`n"
}

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    $env:MSSQL_SA_PASSWORD = [guid]::NewGuid().ToString('N') + 'aA9!'
    $env:SQLCMDPASSWORD = $env:MSSQL_SA_PASSWORD
    $env:CASHMOVEMENTS_SQL_PORT = [string]$Port
    $existingContainer = Invoke-Tool docker @('ps', '-aq', '--filter', "label=com.docker.compose.project=$project")
    $existingVolume = Invoke-Tool docker @('volume', 'ls', '-q', '--filter', "name=$volume")
    $existingNetwork = Invoke-Tool docker @('network', 'ls', '-q', '--filter', "name=$network")
    if ($existingContainer -or $existingVolume -or $existingNetwork) { throw "Verification project already exists. Aborting." }
    $ownsProject = $true
    Write-Host "Checking disposable project $project at 127.0.0.1:$Port."
    Invoke-Compose @('up', '-d', '--wait', 'db') | Out-Null
    $dbContainer = (Invoke-Compose @('ps', '-q', 'db')).Trim()
    if ($dbContainer -notmatch '^[a-f0-9]{12,64}$') { throw 'Expected exactly one disposable SQL container.' }
    Invoke-Database "IF DB_ID(N'CashMovements') IS NOT NULL THROW 51001, 'Database already exists.', 1;" master
    Invoke-Compose @('run', '--rm', 'db-init') | Out-Null
    Invoke-Database @'
IF (SELECT snapshot_isolation_state FROM sys.databases WHERE database_id=DB_ID()) <> 1
    THROW 51002, 'Snapshot isolation is disabled.', 1;
IF (SELECT COUNT(*) FROM dbo.Accounts) <> 4 OR (SELECT COUNT(*) FROM dbo.Movements) <> 2 OR
   (SELECT COUNT(*) FROM dbo.__EFMigrationsHistory) <> 1 OR
   NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE AccountId=N'ACC-JPY' AND Currency=N'JPY') OR
   NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE AccountId=N'ACC-BHD' AND Currency=N'BHD') OR
   NOT EXISTS (SELECT 1 FROM dbo.Accounts WHERE AccountId=N'ACC-ZAR' AND Currency=N'ZAR') OR
   NOT EXISTS (SELECT 1 FROM dbo.Movements WHERE AccountId=N'ACC-001' AND Currency=N'ZAR'
               GROUP BY AccountId HAVING COUNT(*)=2 AND SUM(Amount)=12249.50)
    THROW 51003, 'Unexpected baseline fixture.', 1;
INSERT INTO dbo.Movements(AccountId, ExternalRef, Currency, Amount, OccurredAtUtc, Narration)
VALUES(N'ACC-ZAR', N'VERIFY/SENTINEL', N'ZAR', 37.25, '2024-11-01T12:00:00.1234567', N'Preserve this posting.');
'@
    Write-Host 'PASS: clean database, snapshot isolation, four accounts and two seeded movements.'
    Test-Rerun setup

    Invoke-Database @'
SET XACT_ABORT OFF;
BEGIN TRANSACTION;
DECLARE @probes TABLE (id int IDENTITY, reference nvarchar(128), currency nvarchar(3), amount decimal(19,4), isDuplicate bit);
INSERT INTO @probes (reference, currency, amount, isDuplicate) VALUES
    (N'VERIFY/SENTINEL', N'ZAR', 1, 1),        -- per-account duplicate
    (N'VERIFY/FK', N'JPY', 1, 0),               -- different account currency
    (N'VERIFY/PRECISION', N'ZAR', 0.001, 0),    -- smaller than a cent
    (N'verify/lower', N'ZAR', 1, 0),            -- lowercase direct write
    (N'VERIFY/SPACE ', N'ZAR', 1, 0),           -- trailing space
    (N'-/_', N'ZAR', 1, 0);                    -- missing alphanumeric
DECLARE @probe int = 1;
WHILE @probe <= (SELECT COUNT(*) FROM @probes)
BEGIN
    BEGIN TRY
        INSERT INTO dbo.Movements (AccountId, ExternalRef, Currency, Amount, OccurredAtUtc, Narration)
        SELECT N'ACC-ZAR', reference, currency, amount, '2024-10-01', N'Constraint probe'
        FROM @probes WHERE id=@probe;
        THROW 51005, 'Database unexpectedly accepted a constraint probe.', 1;
    END TRY
    BEGIN CATCH
        IF (SELECT isDuplicate FROM @probes WHERE id=@probe) = 1
        BEGIN
            IF ERROR_NUMBER() NOT IN (2601, 2627) THROW;
        END
        ELSE IF ERROR_NUMBER() <> 547 THROW;
    END CATCH;
    SET @probe += 1;
END;
ROLLBACK;
'@
    Write-Host 'PASS: SQL rejected duplicate references, a mismatched currency, fractional cents and invalid identifiers.'

    Invoke-Database "EXEC(N'CREATE DATABASE [CashMovementsEfVerify]');" master
    $env:ConnectionStrings__CashMovements = "Server=127.0.0.1,$Port;Database=CashMovementsEfVerify;User ID=sa;Password=$env:MSSQL_SA_PASSWORD;Encrypt=True;TrustServerCertificate=True"
    $infra = 'src/CashMovements.Infrastructure'
    Invoke-Tool dotnet @('ef','database','update','--no-build','--configuration','Release','--project',$infra,'--startup-project',$infra) | Out-Null
    if ((Get-SchemaSignature CashMovements) -cne (Get-SchemaSignature CashMovementsEfVerify)) {
        throw 'Generated SQL and EF migration created different schemas or migration history.'
    }
    Write-Host 'PASS: schema script and EF migration agree on columns, indexes/keys, constraints and migration history.'

    Invoke-Compose @('run', '--rm', 'db-init', 'large-demo') | Out-Null
    Invoke-Database @'
IF NOT EXISTS (SELECT 1 FROM dbo.Movements WHERE AccountId=N'ACC-DEMO' AND OccurredAtUtc<'2024-07-01'
               HAVING COUNT_BIG(*)=1 AND SUM(Amount)=100.00) OR
   NOT EXISTS (SELECT 1 FROM dbo.Movements WHERE AccountId=N'ACC-DEMO' AND OccurredAtUtc>='2024-07-01' AND OccurredAtUtc<'2024-08-01'
               HAVING COUNT_BIG(*)=50001 AND SUM(Amount)=0.50)
    THROW 51004, 'Unexpected large statement fixture.', 1;
'@
    Write-Host 'PASS: 50,001 July movements, R100.00 opening and R100.50 closing.'
    Test-Rerun large-demo
}
finally {
    try {
        if ($ownsProject -and $project -match '^cashverify-[a-f0-9]{32}$') {
            Invoke-Compose @('down', '--volumes') | Out-Null
        }
    }
    finally {
        foreach ($envName in $envNames) { [Environment]::SetEnvironmentVariable($envName, $oldEnvironment[$envName], 'Process') }
        Pop-Location
    }
}
