# Cash Movements Service

**Step 4 checkpoint.** The service implements movement submission, current balances and streaming statements through gRPC against SQL Server in Docker. [Local setup](README.md) · [Statement verification](PLANNING.md#step-4-verification)

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

**Account existence.** An account and its currency must exist before its first movement. A well-formed unknown account ID yields the machine-readable `AccountNotFound` outcome on all three RPCs. A rejected submission leaves the ledger untouched. This prevents a mistyped ID from opening an account and posting cash to it. An existing account with no movements has a zero balance in its own currency. Its statement contains the opening header and zero rows. The seed script supplies valid accounts with canonical IDs and fixed currencies so a reviewer can submit a first movement immediately after setup.

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

**Business outcomes and gRPC.** The `UseCases` layer exposes explicit, machine-readable outcomes using enums independent of gRPC. The API translates them into response fields and gRPC status codes. Callers identify a new submission and a successful retry from the disposition, or a conflict from the binary error trailer. All three endpoints share the error mapping and unexpected-exception policy at the API boundary.

**Structured logging.** Use the standard .NET logging abstraction with structured properties for the operation, outcome and trace or correlation ID. The correlation ID connects events from one call across layers. Log routine outcomes at informational or debug level. An identical retry is an expected success. A conflicting submission is a business outcome and can warrant a warning. Log an unexpected failure once at the server boundary, where the call context is available. Keep diagnostic exception details on the server and send a safe error to the caller. Treat cancellation attributable to the client as an expected termination. Routine logs omit request bodies, narration, amounts and credentials.

**Selected console extension.** After the required service functionality, a small console client will stream the existing statement RPC to a local CSV with progress and cancellation. The caller's machine owns the output file. The client publishes a completed file after the RPC succeeds and local writes finish. The [client design and CSV balance rules](IMPLEMENTATION_PLAN.md#console-client-and-local-csv) cover partial files and empty ranges.

## Agreed amount transport

Protobuf has no built-in decimal scalar. We chose an invariant decimal string for money on the wire. For example, a caller can submit `"125.5000"` for ZAR and read back `"125.50"`. This keeps the contract small and the amount readable and exact. Protobuf enforces the string type but cannot establish that its contents are a valid amount. [Protobuf scalar types](https://protobuf.dev/programming-guides/proto3/#scalar)

At the service boundary, `LedgerService.SubmitAsync` calls `CurrencyRules.TryReadMovementAmount` once to validate the original digits and convert them to C# `decimal`. The grammar accepts an optional leading minus, ASCII digits and an optional fractional part with digits on both sides of the decimal point. Whitespace, exponents and grouping separators are invalid. Ignoring insignificant zeros, a nonzero movement must fit SQL `decimal(19,4)` and the currency’s minor unit. A ZAR amount of `"125.501"` fails before conversion could round it. Business arithmetic and retry comparisons use C# `decimal`, and SQL Server stores the movement as `decimal(19,4)`. Binary floating-point types (`float` and `double`) cannot represent every decimal cent exactly and are excluded from the money path.

Two exact alternatives are a structured money message carrying a signed integer coefficient and scale (12550 at scale 2 means 125.50), or a signed integer count of currency minor units (12550 means 125.50 ZAR, 12550 JPY or 12.550 BHD). The first needs rules for coefficient width, allowed scales and equivalent encodings. The second requires every client to apply the correct scale for the currency. Both require conversion logic. For this assessment, validating one readable string at entry keeps that responsibility in the service.

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

## How submission and balance work

1. `SubmitMovement` checks that the protobuf timestamp can be converted exactly to UTC ticks. The use case validates both identifiers, currency, the original amount digits, timestamp and narration before contacting SQL Server. A malformed retry therefore returns its validation error even if the key already exists. An amount expressed in another supported currency must be valid for that submitted currency to reach duplicate classification.
2. For an existing account, the store looks up the canonical account/reference pair. C# record equality compares the retrieved fields, including ordinal narration. This preserves distinctions such as a trailing space that a SQL string equality test can overlook. A valid different currency on an existing key yields `Conflict`. The same currency difference on a fresh key yields `AccountCurrencyMismatch`.
3. `SaveChangesAsync` commits a fresh movement. If competing inserts hit SQL error 2601 or 2627, the losing request disposes its failed context and rereads the exact key using a fresh context. Only a confirmed winner can yield `AlreadyRecorded` or `Conflict`. A failure without that row propagates. This costs an additional read on a contested insert and requires no process-wide lock.
4. `GetBalance` starts from the account and left-joins its movements. One parameterised SQL query sums signed entries, coalesces the empty sum to zero and uses `TRY_CONVERT(decimal(28,4), …)` to detect an out-of-range sum. The result distinguishes an unknown account, a known empty account and an unreportable balance. Every successful reply uses the account currency's fixed minor-unit precision.

The host maps use-case errors to a status and a protobuf reason in `cash-error-bin`. A shared interceptor returns a `trace-id` trailer and logs the operation, outcome and trace for unary and streaming calls. It handles expected conflicts, client cancellation, recognised temporary SQL failures and unexpected exceptions separately. Runtime EF logs are suppressed to keep expected unique-key races and duplicate exception diagnostics out of routine logs. Temporary failures return `UNAVAILABLE`. A caller with an uncertain write outcome can resubmit the same key and payload.

## How statement streaming works

The use case validates the canonical account and both UTC instants before starting the read. Within one explicit SQL Server `SNAPSHOT` transaction, the store looks up the account and sums movements strictly before the lower bound. A left join keeps known accounts with no earlier activity in the result. An unknown account or unreportable opening fails before the header.

The range query uses the same context and transaction, includes the lower bound, excludes the upper bound, and orders by UTC occurrence time followed by movement ID. The second key gives equal timestamps a stable order. A backdated write committed after the opening was read first appears in a subsequent export.

The statement index has keys `(AccountId, OccurredAtUtc, MovementId)` and includes `ExternalRef`, `Currency`, `Amount` and `Narration` to cover the row projection. With the original index, SQL Server chose a clustered scan and sort for both measured ranges. With the included columns, both used an index seek in statement order. The wider index costs storage and adds work to each insert. It lets SQL Server serve these exports directly from the ordered index. [Measured plans](PLANNING.md#step-4-verification)

EF reads an untracked six-field projection with asynchronous enumeration. The provider uses its non-retrying execution strategy. The store retains the running decimal total and checks its range and minor-unit precision before emitting each row. The endpoint awaits each write before advancing the enumerator. The generated test client also reads one frame at a time. These choices keep the application's retained data independent of statement length. A slow reader holds a SQL connection and snapshot longer, which can increase row-version retention. [EF Core buffering and streaming](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying#buffering-and-streaming)

The iterator owns the reader, transaction and context. Completion ends the snapshot. Cancellation, a failed write or an unreportable running balance disposes the iterator and releases those resources. Only a successful terminal gRPC status confirms closing. For a retry, the caller starts a new export and uses its new opening and rows together.
