SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @accounts TABLE (AccountId nvarchar(64) PRIMARY KEY, Currency nvarchar(3));
INSERT INTO @accounts (AccountId, Currency) VALUES
    (N'ACC-001', N'ZAR'),
    (N'ACC-ZAR', N'ZAR'),
    (N'ACC-JPY', N'JPY'),
    (N'ACC-BHD', N'BHD');

-- Keep all existing business data, including records added between setup runs.
INSERT INTO dbo.Accounts (AccountId, Currency)
SELECT seed.AccountId, seed.Currency
FROM @accounts AS seed
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Accounts AS existing WITH (UPDLOCK, HOLDLOCK)
    WHERE existing.AccountId = seed.AccountId COLLATE Latin1_General_100_BIN2
);

DECLARE @movements TABLE (
    ExternalRef nvarchar(128) PRIMARY KEY,
    Amount decimal(19,4),
    OccurredAtUtc datetime2(7),
    Narration nvarchar(256)
);
INSERT INTO @movements (ExternalRef, Amount, OccurredAtUtc, Narration) VALUES
    (N'MOV-20240715-000123', 12500.00, '2024-07-15T10:42:31', N'Initial deposit'),
    (N'MOV-20240716-000124', -250.50, '2024-07-16T09:00:00', N'Office withdrawal');

INSERT INTO dbo.Movements (AccountId, ExternalRef, Currency, Amount, OccurredAtUtc, Narration)
SELECT N'ACC-001', seed.ExternalRef, N'ZAR', seed.Amount, seed.OccurredAtUtc, seed.Narration
FROM @movements AS seed
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Movements AS existing WITH (UPDLOCK, HOLDLOCK)
    WHERE existing.AccountId = N'ACC-001' AND existing.ExternalRef = seed.ExternalRef COLLATE Latin1_General_100_BIN2
);

COMMIT;
GO
