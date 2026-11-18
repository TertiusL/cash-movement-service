SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM dbo.Accounts WITH (UPDLOCK, HOLDLOCK) WHERE AccountId = N'ACC-DEMO')
    INSERT INTO dbo.Accounts (AccountId, Currency) VALUES (N'ACC-DEMO', N'ZAR');

-- Sequence 0 is the June opening. July has 25,000 debit/credit pairs and a final R0.50 credit.
WITH fixture AS (
    SELECT
        CONCAT(N'DEMO-', seq.value) AS ExternalRef,
        CAST(CASE
            WHEN seq.value = 0 THEN 100.00
            WHEN seq.value = 50001 THEN 0.50
            WHEN seq.value % 2 = 1 THEN -1.00
            ELSE 1.00
        END AS decimal(19,4)) AS Amount,
        CASE WHEN seq.value = 0 THEN CAST('2024-06-30T23:59:59' AS datetime2(7))
             ELSE DATEADD(second, seq.value - 1, CAST('2024-07-01T00:00:00' AS datetime2(7)))
        END AS OccurredAtUtc,
        CASE WHEN seq.value = 0 THEN N'Opening deposit'
             WHEN seq.value = 50001 THEN N'Final credit'
             WHEN seq.value % 2 = 1 THEN N'Demo withdrawal'
             ELSE N'Demo deposit'
        END AS Narration
    FROM GENERATE_SERIES(0, 50001) AS seq
)
INSERT INTO dbo.Movements (AccountId, ExternalRef, Currency, Amount, OccurredAtUtc, Narration)
SELECT N'ACC-DEMO', seed.ExternalRef, N'ZAR', seed.Amount, seed.OccurredAtUtc, seed.Narration
FROM fixture AS seed
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Movements AS existing WITH (UPDLOCK, HOLDLOCK)
    WHERE existing.AccountId = N'ACC-DEMO' AND existing.ExternalRef = seed.ExternalRef COLLATE Latin1_General_100_BIN2
);

COMMIT;
GO
