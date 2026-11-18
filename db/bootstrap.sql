USE [master];
GO
SET NOCOUNT ON;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock
    @Resource = N'CashMovements:setup',
    @LockMode = 'Exclusive',
    @LockOwner = 'Session',
    @LockTimeout = 60000;
IF @lockResult < 0
    THROW 51000, 'Could not acquire the CashMovements setup lock.', 1;

IF DB_ID(N'CashMovements') IS NULL
    EXEC(N'CREATE DATABASE [CashMovements]');
GO
IF EXISTS (SELECT 1 FROM sys.databases WHERE name = N'CashMovements' AND snapshot_isolation_state <> 1)
    ALTER DATABASE [CashMovements] SET ALLOW_SNAPSHOT_ISOLATION ON;
GO
USE [CashMovements];
GO
