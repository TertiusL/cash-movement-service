IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118113139_InitialLedger'
)
BEGIN
    CREATE TABLE [dbo].[Accounts] (
        [AccountId] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Currency] nvarchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
        CONSTRAINT [PK_Accounts] PRIMARY KEY ([AccountId]),
        CONSTRAINT [AK_Accounts_AccountId_Currency] UNIQUE ([AccountId], [Currency]),
        CONSTRAINT [CK_Accounts_AccountId] CHECK (DATALENGTH(REPLACE(TRANSLATE([AccountId], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_', REPLICATE(N'X', 39)), N'X', N'')) = 0
    AND [AccountId] LIKE N'%[A-Z0-9]%'),
        CONSTRAINT [CK_Accounts_Currency] CHECK (DATALENGTH([Currency]) = 6 AND [Currency] IN (N'ZAR', N'JPY', N'BHD'))
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118113139_InitialLedger'
)
BEGIN
    CREATE TABLE [dbo].[Movements] (
        [MovementId] bigint NOT NULL IDENTITY,
        [AccountId] nvarchar(64) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [ExternalRef] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Currency] nvarchar(3) COLLATE Latin1_General_100_BIN2 NOT NULL,
        [Amount] decimal(19,4) NOT NULL,
        [OccurredAtUtc] datetime2(7) NOT NULL,
        [Narration] nvarchar(256) NOT NULL,
        CONSTRAINT [PK_Movements] PRIMARY KEY ([MovementId]),
        CONSTRAINT [CK_Movements_AccountId] CHECK (DATALENGTH(REPLACE(TRANSLATE([AccountId], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_', REPLICATE(N'X', 39)), N'X', N'')) = 0
    AND [AccountId] LIKE N'%[A-Z0-9]%'),
        CONSTRAINT [CK_Movements_Amount_MinorUnit] CHECK (([Currency] = N'ZAR' AND [Amount] % 0.01 = 0) OR
    ([Currency] = N'JPY' AND [Amount] % 1 = 0) OR
    ([Currency] = N'BHD' AND [Amount] % 0.001 = 0)),
        CONSTRAINT [CK_Movements_Amount_Nonzero] CHECK ([Amount] <> 0),
        CONSTRAINT [CK_Movements_ExternalRef] CHECK (DATALENGTH(REPLACE(TRANSLATE([ExternalRef], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_', REPLICATE(N'X', 39)), N'X', N'')) = 0
    AND [ExternalRef] LIKE N'%[A-Z0-9]%'),
        CONSTRAINT [FK_Movements_Accounts_AccountId_Currency] FOREIGN KEY ([AccountId], [Currency]) REFERENCES [dbo].[Accounts] ([AccountId], [Currency]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118113139_InitialLedger'
)
BEGIN
    CREATE INDEX [IX_Movements_AccountId_Currency] ON [dbo].[Movements] ([AccountId], [Currency]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118113139_InitialLedger'
)
BEGIN
    CREATE INDEX [IX_Movements_AccountId_OccurredAtUtc_MovementId] ON [dbo].[Movements] ([AccountId], [OccurredAtUtc], [MovementId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118113139_InitialLedger'
)
BEGIN
    CREATE UNIQUE INDEX [UX_Movements_AccountId_ExternalRef] ON [dbo].[Movements] ([AccountId], [ExternalRef]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261118113139_InitialLedger'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261118113139_InitialLedger', N'8.0.34');
END;
GO

COMMIT;
GO

