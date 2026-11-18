# Cash Movements Service

**Foundation checkpoint.** The candidate approved all design defaults. Steps 1 and 2 delivered the solution and its dependency graph, the protobuf contract, EF Core mappings and migration, and the Docker SQL Server setup. [Local setup](README.md) · [Checks actually run](PLANNING.md#foundation-verification)

## Design decisions agreed during planning

**Account currency.** Each account has one fixed currency. Different accounts may use different currencies. A movement must match its account currency. This gives the balance a meaningful unit and lets us sum deposits and withdrawals without an exchange-rate policy.

**Supported currencies.** The brief gives ZAR as an example and leaves the currency list open. We chose ZAR, JPY and BHD for this implementation because their different minor units exercise account-specific posting precision. Amounts are validated by numeric value, so trailing zeros are harmless. Supporting another currency requires an explicit minor-unit rule. [Currency minor units](https://docs.adyen.com/development-resources/currency-codes/)

| Account currency | Smallest posting unit | Valid amount | Rejected without rounding |
|---|---:|---:|---:|
| ZAR | 0.01 | 125.50 | 125.501 |
| JPY | 1 | 125.0000 | 125.1 |
| BHD | 0.001 | 125.001 | 125.0001 |

**Currency-code spelling.** Validate the input as exactly three ASCII letters. Reject whitespace anywhere, including leading and trailing spaces. Convert the letters to uppercase using invariant casing, then accept only `ZAR`, `JPY` or `BHD`. Store, compare and return the uppercase code. This gives each currency one representation across account checks, retries and responses. A casing-only change on an otherwise identical retry yields `AlreadyRecorded`. Changing to a different supported currency on an otherwise valid retry yields `Conflict`.

**UTC and reporting boundaries.** Use UTC instants for movement timestamps, persistence, comparisons, responses and both statement range boundaries. For example, `2024-07-16T00:30:00+02:00` and `2024-07-15T22:30:00Z` identify the same instant. An otherwise identical retry yields `AlreadyRecorded` even though the submitted representations have different offsets and calendar dates. Responses represent the stored instant in UTC.

Callers resolve local movement times and both ends of a local reporting period into unambiguous instants before submission. They also handle local display. Regional and account timezones, daylight-saving rules and local-calendar reporting are outside the service's scope. A local time with no offset or timezone is insufficient to identify an instant. The service never infers a timezone from an account's currency or the server's local timezone. This keeps retries and statement queries consistent across deployments.

**Account existence.** An account and its currency must exist before its first movement. For a well-formed account ID that does not exist, submission, balance lookup and statement export return the machine-readable `AccountNotFound` outcome. A rejected submission creates neither an account nor a movement. This prevents a mistyped ID from opening an account and posting cash to it. An existing account with no movements has a zero balance in its own currency and a successful empty statement. The seed script will supply valid accounts with canonical IDs and fixed currencies so a reviewer can submit a first movement immediately after setup.

**Movement identity and retries.** A movement is identified by the pair `(accountId, externalRef)`. The first successfully recorded movement is authoritative, including when submissions race. This gives a caller that lost its response a safe way to retry.

| Submission | Business outcome | Effect on the ledger |
|---|---|---|
| First valid submission for a new key | `Recorded` | Store one movement and count its signed amount once. |
| Same key and equivalent amount, currency, occurrence time and narration | `AlreadyRecorded` (success) | Return success with the original movement and balance unchanged. |
| Same key and a difference in any of those four fields on an otherwise valid submission | `Conflict` | Preserve the original movement and balance. |

Narration appears on statements. Accepting a changed description as a successful retry would mislead the caller about what an operator will read. Corrections and updates to recorded movements are outside the scope of this service.

**Interpretation of the brief.** We interpret “record cash movements” as entering events that have already occurred. This assumption supports accepting a negative recorded balance and recording each reported side of a transfer independently.

**Transfers.** Each reported side is an independent movement. A debit of R200 on account A and a credit of R200 on account B can both use `TRF-42` because the reference is unique per account. Success on A confirms only A's debit. Transfer execution and coordination are outside this service's scope. The caller is responsible for retrying individual submissions and reconciling the recorded sides against the expected movements.

If B's submission fails or its response is lost, the caller can resubmit B's original account, reference and movement details. For an otherwise valid submission, an absent B row yields `Recorded`, an equivalent committed B row yields `AlreadyRecorded`, and a committed B row with different details yields `Conflict`. Persisted movements determine recording status. Logs help trace an attempt when investigating a discrepancy.

**Movement amounts.** Store each signed amount as SQL Server `decimal(19,4)`. Four decimal places let one column accommodate currencies with different minor units. Accept a value only if it fits the column and is an exact multiple of the account currency's minor unit. Validate before any conversion that could round. For ZAR, `125.50` and `125.5000` compare equal. Both `0.001` and `125.501` are invalid because each includes a fraction of a cent. The same rule applies to withdrawals. [SQL Server decimal conversion](https://learn.microsoft.com/en-us/sql/t-sql/data-types/decimal-and-numeric-transact-sql?view=sql-server-ver17) · [Examples of currency minor units](https://docs.adyen.com/development-resources/currency-codes/)

**Zero-value movements.** A zero amount returns `InvalidAmount`. The submission creates no movement or statement row and leaves an unused reference available for a corrected submission. A withdrawal may bring the balance to exactly zero. Memos, notes and reconciliation markers belong in a separate workflow because they do not record a change in cash.

**Negative balances.** Record an otherwise valid withdrawal even if it takes an account below zero or further into a negative balance. The balance is the signed sum of recorded movements. Operations staff may enter an earlier deposit after a later withdrawal, and the order of entry should not prevent either from being recorded. Spending authorisation, overdraft limits and debt handling are outside this service's scope.

**Business outcomes and gRPC.** The `UseCases` layer will expose explicit, machine-readable outcomes using enums independent of gRPC. The API will translate them into response fields and gRPC status codes. Callers must be able to distinguish a new submission, a successful retry and a conflict without inspecting message text. Shared error mapping and unexpected-exception handling belong at the API boundary so endpoints apply the same policy. This is an in-process responsibility.

**Structured logging.** Use the standard .NET logging abstraction with structured properties for the operation, outcome and trace or correlation ID. The correlation ID connects events from one call across layers. Log routine outcomes at informational or debug level. An identical retry is an expected success. A conflicting submission is a business outcome and can warrant a warning. Log an unexpected failure once at the server boundary, where the call context is available. Keep diagnostic exception details on the server and send a safe error to the caller. Treat cancellation attributable to the client as an expected termination. Routine logs omit request bodies, narration, amounts and credentials.

**Selected console extension.** After the required service functionality, a small console client will stream the existing statement RPC to a local CSV with progress and cancellation. The caller's machine owns the output file. The client publishes a completed file after the RPC succeeds and local writes finish. The [client design and CSV balance rules](IMPLEMENTATION_PLAN.md#console-client-and-local-csv) cover partial files and empty ranges.

## Agreed balance and statement contract

**Aggregate precision.** Keep movements at `decimal(19,4)` and use standard .NET `decimal` for balances. The agreed aggregate range of `decimal(28,4)` permits up to 24 integral digits and four fractional digits. The absolute value must be below 10²⁴. Convert SQL current and opening sums at the same scale before reading them into .NET. Check the range of each running total and its exact representability at the currency's minor unit before output. Overflow or precision loss ends the read with `OUT_OF_RANGE` and reason `AGGREGATE_OUT_OF_RANGE`. Recorded movements remain authoritative.

**Statement framing.** Send a single leading header with canonical account ID, currency, UTC start (inclusive), UTC end (exclusive) and opening balance from movements before the start. Stream zero or more movements with their after-movement balances from the same SQL snapshot. On gRPC `OK`, closing equals the last running balance, or the header's opening for a period with no movements. The CSV takes its opening from the header, counts only movements and writes its closing only after successful completion. An empty period can therefore show the same nonzero balance at both ends.

## Agreed identifier format

**Format.** Both `accountId` and `externalRef` accept ASCII letters, digits, hyphens (`-`), underscores (`_`) and forward slashes (`/`). Each identifier must contain at least one ASCII letter or digit. Reject whitespace anywhere and all characters outside this set. Validate the original input, then convert ASCII letters to uppercase for storage, output and lookup. Preserve punctuation and leading zeros exactly.

**Rationale and trade-off.** The brief gives examples and requires movement references to be unique per account. It leaves identifier format and case sensitivity unspecified. A caller retrying `stmt/1004/08` as `STMT/1004/08` on the same account addresses the same movement, which prevents a casing change from posting cash twice. Supporting slashes and underscores accommodates references such as `STMT/1004/08` and `STMT_1004/08`. Those two references remain distinct. Only the uppercase spelling is retained. An upstream system that distinguishes identifiers solely by case or uses unsupported characters cannot use those identifiers as issued.

**Lookups and uniqueness.** Use the same validation and normalisation rules for submissions, balance lookups, statements and seed data. Compare canonical identifiers ordinally in .NET and pass the canonical value to EF Core as the lookup parameter. In SQL Server, require non-null identifiers, apply an explicit binary-code-point (`_BIN2`) collation and use constraints to enforce the permitted uppercase characters and the presence of at least one letter or digit. Check trailing spaces explicitly because SQL Server pads strings for most equality comparisons. [String comparison in .NET](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-strings) · [SQL Server collations](https://learn.microsoft.com/en-us/sql/relational-databases/collations/collation-and-unicode-support?view=sql-server-ver17) · [SQL Server string comparisons](https://learn.microsoft.com/en-us/sql/t-sql/language-elements/string-comparison-assignment?view=sql-server-ver17)

SQL Server must enforce uniqueness of the canonical account ID and of the movement pair `(accountId, externalRef)`. Different accounts may reuse the same reference. If concurrent submissions resolve to the same pair, only one movement can commit. On that movement's unique-key violation, compare the four submitted fields with the committed row to determine the retry or conflict outcome.

## Agreed field comparison

The candidate approved both comparison rules. Each comparison must give the same result after a database round trip.

| Field | Agreed rule and rationale |
|---|---|
| `occurredAt` precision | Use 100-nanosecond resolution. Accept instants exactly representable at that resolution and reject values that would lose precision on conversion. An otherwise identical retry differing by one tick yields `Conflict`. |
| `narration` | Preserve the submitted text and compare it ordinally. Case, whitespace, line endings and Unicode representation are significant. A trailing space changes the statement text and therefore produces a conflict. |

Protobuf timestamps can express nanoseconds. The agreed 100-nanosecond precision matches a .NET tick and can be stored exactly in SQL Server using UTC with `datetime2(7)`. [Protobuf Timestamp](https://protobuf.dev/reference/protobuf/google.protobuf/#timestamp) · [.NET ticks](https://learn.microsoft.com/en-us/dotnet/api/system.datetime.ticks?view=net-8.0) · [SQL Server datetime2](https://learn.microsoft.com/en-us/sql/t-sql/data-types/datetime2-transact-sql?view=sql-server-ver17)

The [implementation plan](IMPLEMENTATION_PLAN.md) defines the approved architecture, statement semantics, verification steps and assessment checklist. The [planning notes](PLANNING.md) record the decision history.
