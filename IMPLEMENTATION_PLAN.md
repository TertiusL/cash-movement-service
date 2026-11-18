# Cash Movements Service — approved implementation plan

**Status.** The candidate approved the revised plan and its defaults on 18 November 2026. Steps 1–4 are complete. Statement streaming is ready for review. The console CSV client is step 5. [Foundation verification](PLANNING.md#foundation-verification) · [Step 3 verification](PLANNING.md#step-3-verification) · [Step 4 verification](PLANNING.md#step-4-verification)

## 1. Agreed scope and business rules

| Area | Decision to preserve |
|---|---|
| Platform and architecture | Deliver C# on .NET 8, gRPC with protobuf and SQL Server. Use Rider with the .NET CLI and Docker Desktop on Windows. Use Clean Architecture with a layer named `UseCases`. EF Core is preferred subject to proving concurrency and streaming behaviour. |
| Accounts and amounts | An account must exist and has one fixed currency. Unknown accounts yield `AccountNotFound`. A known empty account has a zero balance and zero statement movements. Store signed movements as `decimal(19,4)`. Our implementation supports ZAR, JPY and BHD with increments of 0.01, 1 and 0.001 respectively. The brief gives ZAR as an example and leaves the supported set to the implementation. Reject fractional minor units without rounding. Zero yields `InvalidAmount` and leaves a new reference available. Negative balances are valid. Balance is the sum of all recorded movements for the account. |
| Identifiers and currency codes | Account IDs and references accept ASCII letters, digits, hyphens, underscores and forward slashes, with at least one letter or digit. Reject whitespace. Uppercase letters for storage, lookup and output. Preserve punctuation and leading zeros. Currency input is exactly three ASCII letters, uppercased and checked against the supported set. |
| Immutable movements | The canonical pair `(accountId, externalRef)` identifies one movement. The first committed movement wins. Equivalent amount, currency, occurrence time and narration yield successful `AlreadyRecorded`. A valid change to any of those fields yields `Conflict` and leaves the original intact. Narration appears on statements. Corrections and updates are outside scope. |
| Transfers | Record each reported side independently. The caller owns retries and reconciliation. Success for one account establishes only that side's recording status. Persisted movements determine the outcome. Logs support investigation. |
| Time | Store, compare and return UTC instants. Both statement boundaries are instants. Equivalent offsets identify the same time. Callers handle regional timezones, daylight-saving rules, local reporting periods and display. |
| Outcomes and diagnostics | Use transport-independent business outcomes in `UseCases`. Centralise gRPC error mapping and unexpected-exception handling. Include structured logging through the standard .NET abstraction. Correlate events, distinguish client cancellation, log unexpected failures once on the server and keep responses safe. Routine logs omit full requests, narration, amounts and credentials. |
| Selected optional client | After the required service works, add a small .NET console utility that consumes the existing statement RPC, shows progress and writes a local CSV. The caller selects the destination on the client machine. |

**Scope interpretation.** We interpret the brief's description of staff recording movements as a ledger of events that have already occurred. This interpretation underlies two agreed decisions. An otherwise valid withdrawal can take the recorded balance below zero, and each reported side of a transfer can be recorded independently. Spending authorisation, transfer execution and cross-account coordination are outside the chosen scope.

The full rationales are in [SOLUTION.md](SOLUTION.md). The discussion, prompts and planned tests are in [PLANNING.md](PLANNING.md).

## 2. Agreed defaults

| Decision | Decision and trade-off |
|---|---|
| Timestamp precision | Use `google.protobuf.Timestamp` and SQL Server `datetime2(7)` in UTC. Require movement times and range bounds to be present, valid and exactly representable at 100 ns. Check protobuf nanoseconds before conversion to .NET ticks. Reject finer values that would lose information. This preserves exact retries across storage at the cost of rejecting some valid protobuf instants. |
| Text and field limits | Cap account IDs at 64 and references at 128 ASCII characters. Require narration with at least one non-whitespace character and at most 256 UTF-16 code units. Preserve all accepted text, including edge spaces, case, line endings and Unicode representation. Compare narration ordinally after reading it. A trailing space on an otherwise valid narration changes the statement and causes a conflict. These lengths are assessment assumptions. |
| Amount representation | Use invariant decimal strings in protobuf. Accept an optional leading minus, ASCII digits and an optional decimal point with digits on both sides. Ignore insignificant fractional trailing zeros when checking representability. Reject whitespace, exponents, grouping separators, overflow and any value requiring rounding. Check the original digits before conversion. Use the full movement column range subject to the currency rule. Output fixed minor-unit precision for the account currency. Strings need validation but preserve decimal values exactly across clients. |
| Statement range | Require both UTC bounds and `fromInclusive < toExclusive`. Include movements at the start and exclude the end. Adjacent ranges can be joined without omissions or duplicates. |
| Statement balances | Opening is the sum of signed amounts with `occurredAt < fromInclusive` for the account, or zero. Send it in the first stream message alongside account, currency and UTC bounds. Each subsequent movement reports its after-movement running balance. On successful completion, closing equals the last running balance, or the header's opening if the period contains zero movements. All values come from the same snapshot. |
| Ordering and consistency | Order by `(OccurredAtUtc, MovementId)` ascending. The generated ID is a stable tie-breaker for equal timestamps. It carries no promise about commit order. The account lookup, opening sum and stream use one SQL Server snapshot. A backdated movement committed after the snapshot appears in a subsequent export. A slow export holds a connection and can retain row versions longer. |

## 3. Project responsibilities and dependencies

| Project | Owns | Depends on |
|---|---|---|
| `CashMovements.Domain` | Canonical account IDs, references, supported currency and minor-unit rules, signed movement values, and equality of recorded fields. | .NET only |
| `CashMovements.UseCases` | Submit, get balance and export statement. Own the outcome enums, read models and small persistence interfaces shaped around those operations. Specify that recording is atomic and the statement is a cancellable asynchronous sequence. | Domain |
| `CashMovements.Infrastructure` | EF Core 8 SQL Server provider, mappings, migrations, unique-key recovery, aggregates and the lifetime of the snapshot reader. Translate expected database conditions into the outcomes specified by UseCases. | UseCases and Domain |
| `CashMovements.Grpc` | Versioned protobuf contract, generated types, request/response mapping, three endpoints, shared status mapping, exception interceptor and dependency injection. | UseCases and Infrastructure for registration |
| `CashMovements.StatementClient` (extension) | Command-line arguments, incremental RPC consumption, CSV output, progress and cancellation. Build after the service is verified. | .NET 8 gRPC client generated from the same versioned protobuf file. No dependency on the host, EF Core or SQL Server. |
| Tests | Fast domain examples plus real SQL Server integration tests. Exercise the host over loopback gRPC with the generated client for contract and streaming checks. | Projects under test |

The write interface needs an atomic “record or classify existing” operation. The query interface needs account balance and statement reads. This keeps `DbContext`, SQL exceptions and protobuf types out of business logic. Direct handler calls and EF Core's existing unit-of-work behaviour are sufficient for this scope.

## 4. SQL Server, EF Core and the write race

| Table | Columns and keys |
|---|---|
| `Accounts` | `AccountId nvarchar(64)` primary key, `Currency nvarchar(3)` required. A unique alternate key on `(AccountId, Currency)` gives movements a foreign-key target that enforces their account currency. |
| `Movements` | `MovementId bigint identity` primary key. The six required fields are `ExternalRef nvarchar(128)`, `AccountId nvarchar(64)`, `Currency nvarchar(3)`, `Amount decimal(19,4)`, `OccurredAtUtc datetime2(7)` and `Narration nvarchar(256)`, all non-null. Composite foreign key to the account and currency. Named unique index on `(AccountId, ExternalRef)`. Statement index on `(AccountId, OccurredAtUtc, MovementId)`. |

Map the keys, column types, precision, lengths, required fields and indexes explicitly with EF Core Fluent configuration. Use Unicode columns with a binary-code-point collation for canonical keys and currency codes so checks can reject non-ASCII characters before any lossy code-page conversion. Database checks enforce uppercase allowed identifiers with at least one alphanumeric character, allowed currency codes, nonzero amounts and currency minor units. Validation also runs **before** SQL parameter conversion, since SQL can round a value narrowed to scale four. Materialise `datetime2` as UTC explicitly. The service exposes append and read operations only. Computing balances directly from movements keeps them consistent with the ledger.

**Agreed submit flow**

1. Validate presence and syntax, normalise keys and currency, then validate the amount and timestamp without losing precision. A malformed request fails before duplicate classification.
2. Find the account. Check for an existing canonical movement key and compare its four payload fields. A different supported currency on a valid retry yields `Conflict`. For an unused key, a currency that differs from the account yields `AccountCurrencyMismatch`.
3. Insert one valid movement and return `Recorded` only after commit. The unique index arbitrates simultaneous inserts. Both callers can pass a prior existence check.
4. On SQL Server duplicate-key error 2601 or 2627 from the movement insert, unwind the failed write and reread the exact key from a fresh, untracked context. Classify the committed winner as `AlreadyRecorded` or `Conflict`. If the expected winning row cannot be confirmed, propagate the failure. Other integrity or connectivity errors must not yield duplicate success.

**Agreed aggregate range.** Use standard .NET `decimal` for current, opening, running and closing balances. The agreed `decimal(28,4)` range permits up to 24 integral digits and four fractional digits. The absolute value must be strictly below 10²⁴. Each balance must also be an exact multiple of its currency's minor unit. This bound leaves room for balances much larger than a single `decimal(19,4)` movement and guarantees exact representation at scale four. The bound applies when reading balances.

For balances and openings, SQL Server computes `SUM(Amount)` as `decimal(38,4)`. Coalesce the empty sum for a known account to zero **before** converting the numeric result to `decimal(28,4)` in SQL. Both types have scale four. A failed range conversion yields the typed `AGGREGATE_OUT_OF_RANGE` error. Materialise a successful result directly as .NET `decimal`. Add each movement using checked decimal arithmetic and explicitly enforce the supported bound before emitting the row. Verify exact minor-unit representability before formatting. If a total would overflow or require rounding, fail the read with gRPC `OUT_OF_RANGE`. A failure after the header leaves a partial statement. Decimal strings are used only at the protobuf/CSV boundary. [SQL Server SUM return types](https://learn.microsoft.com/en-us/sql/t-sql/functions/sum-transact-sql?view=sql-server-ver17) · [.NET Decimal](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-decimal)

## 5. Protobuf and gRPC contract

Use a versioned `cashmovements.v1` service. `Movement` carries `externalRef`, `accountId`, `currency`, `amount` (decimal string), `occurredAt` (Timestamp) and `narration`. Validate missing fields through message presence or the agreed domain constraints. Use a reserved zero `UNSPECIFIED` enum value so an unset outcome cannot mean success.

| RPC | Request | Successful response |
|---|---|---|
| `SubmitMovement` (unary) | One `Movement` | `SubmitMovementReply` with typed disposition `RECORDED` or `ALREADY_RECORDED` and the canonical recorded movement. Both use gRPC `OK`. |
| `GetBalance` (unary) | `accountId` | Canonical account ID, currency and exact signed balance. |
| `ExportStatement` (server streaming) | `accountId`, `fromInclusive`, `toExclusive` | Stream `StatementFrame` with a protobuf `oneof`. Exactly one leading `StatementHeader` carries canonical account ID, currency, both UTC bounds and opening balance. Zero or more `StatementRow` frames follow, each with the full canonical movement and its exact running balance. Final gRPC `OK` confirms the closing balance under the rule in section 2. |

**Agreed failure contract.** A shared mapper assigns the gRPC status and a small protobuf `BusinessError` with a reason enum in a documented binary trailer (`cash-error-bin`) for expected business failures. Clients branch on disposition or status plus reason.

| Condition | gRPC status | Typed reason or client behaviour |
|---|---|---|
| Invalid field, unsupported currency, amount, timestamp or range | `INVALID_ARGUMENT` | Specific reason such as `INVALID_AMOUNT`, `UNSUPPORTED_CURRENCY` or `INVALID_RANGE` |
| Valid ID, unknown account | `NOT_FOUND` | `ACCOUNT_NOT_FOUND` |
| Valid retry with changed recorded fields | `ALREADY_EXISTS` | `CONFLICT` |
| New reference with a different supported account currency | `FAILED_PRECONDITION` | `ACCOUNT_CURRENCY_MISMATCH` |
| A current, opening or running total exceeds the supported range or cannot be reported exactly | `OUT_OF_RANGE` | `AGGREGATE_OUT_OF_RANGE`. Preserve the recorded movements. |
| Client cancellation or elapsed deadline | `CANCELLED` or `DEADLINE_EXCEEDED` | Stop work and release resources. A partial stream is incomplete. |
| Recognised temporary database outage or unexpected failure | `UNAVAILABLE` or `INTERNAL`, respectively | Return a safe description and correlation identifier where available. The server owns diagnostics. |

Use `ILogger` structured events with operation, outcome and trace ID. Log normal completions once per call, conflicts at a suitable level, cancellation at debug level, and unexpected exceptions once at the host boundary. Keep EF sensitive-data logging disabled. A stream failure propagates a terminal status even after rows were sent. A caller restarting an export starts a new snapshot and discards partial aggregates. [ASP.NET Core gRPC error handling](https://learn.microsoft.com/en-us/aspnet/core/grpc/error-handling?view=aspnetcore-8.0)

## 6. Streaming 50,000+ rows

Enable `ALLOW_SNAPSHOT_ISOLATION` during database setup. Within one explicit read-only `SNAPSHOT` transaction on one context, check the account, compute the opening and send the header. An unknown account, invalid range or unsupported opening fails before any header is sent. Stream the ordered, no-tracking range projection in the same transaction. A per-statement snapshot alone would let a backdated insert fall between the opening query and the row query. [SQL Server snapshot isolation](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/snapshot-isolation-in-sql-server)

Enumerate using `AsAsyncEnumerable`, update one running total and await each gRPC write before reading ahead. Use a non-retrying EF execution strategy for this read because retrying strategies can buffer the result. Forward cancellation to SQL and through iteration. Dispose the reader, transaction and context on completion, cancellation or failure, including early termination by the client. The generated test client consumes the response stream incrementally, retaining only count, previous key and last total. Memory use is bounded by row size, fixed accumulators and transport buffers. The stream duration still consumes a connection. [EF Core buffering and streaming](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying#buffering-and-streaming)

### Console client and local CSV

The client calls `ExportStatement` with an account and UTC range. It consumes the async response stream once and writes each movement directly to a buffered UTF-8 file in stream order. Keep only the current row, count, opening balance and most recent running balance. Show the movement count and elapsed time on stderr at intervals, with a final count on completion. The total number of entries is unknown until the stream finishes.

**CSV layout.** Use one rectangular table with columns `recordType,accountId,externalRef,currency,amount,occurredAt,narration,runningBalance`. Write the CSV column names, then an `opening` summary directly from the RPC header. Initialise the closing candidate to that opening. For each movement, write all six canonical fields and the supplied running balance, retain that balance as the closing candidate and increment progress. Only after terminal gRPC `OK` write the `closing` summary. Summary records use the account, currency and running-balance columns and leave movement-only fields empty. A period with an opening of R100.00 and no movements produces an opening and a closing of R100.00, with a movement count of zero. No separate balance query is needed.

Write invariant amounts at the currency's minor-unit precision and render timestamps in UTC. Use CRLF record separators. Quote fields containing a comma, a double quote, CR or LF, and double any embedded double quote inside a quoted field. Preserve the exact narration, including Unicode, spaces and embedded newlines. [CSV field and record conventions](https://www.rfc-editor.org/rfc/rfc4180)

**File completion.** Create a unique sibling such as `july.csv.<run-id>.partial` with exclusive creation. Ctrl+C signals the RPC and write loop. On cancellation, a non-OK terminal status, or a local write, flush or rename failure, exit nonzero, report the reason and the partial path if created. A retry starts a fresh file and a new snapshot. After successful end-of-stream, finish the CSV, flush and close the writer, then rename the partial file to the requested name in the same directory without overwriting an existing file. Only after that rename announce success, final count and destination. Receiving the last expected row alone is insufficient.

## 7. Local setup, scripts and evidence

- **Runtime.** Target `net8.0`, pin the installed 8.0.428 SDK and compatible 8.x packages. Use a tested, pinned SQL Server 2022 Developer Linux container image in Compose, a named data volume, loopback port and connection health check. Supply local connection settings through documented environment variables or .NET user secrets.
- **Reproducible SQL.** A bootstrap script creates the database and enables snapshot isolation. EF migrations own schema changes. Commit a reviewed, idempotent schema SQL script generated from those migrations. Run a separate repeatable seed script that creates canonical accounts for all three currencies, including an empty account and a small example history. A separate deterministic large-data fixture creates at least 50,001 in-range rows for one account. Re-running setup must preserve existing movements.
- **README.** Give a copyable Windows PowerShell path from a fresh clone through SDK/Docker checks, local configuration, container startup and readiness, schema and seed scripts, restore, build, test, and host launch via CLI or Rider. State the HTTP/2 endpoint and local transport settings. Include a small request for each RPC using the checked-in protobuf or generated test client, how to run the large fixture/test, expected results, and how to stop the container while retaining its volume.
- **Planned run path.** Set the local database password outside source control. Run `docker compose up -d --wait db`, then `docker compose run --rm db-init` to execute `db/bootstrap.sql`, `db/schema.sql` and `db/seed.sql` in order. Run `dotnet restore`, `dotnet build --no-restore` and `dotnet test --no-build`. Give integration tests an isolated test database. Start the host with `dotnet run --project src/CashMovements.Grpc` or its equivalent Rider run configuration. Document the separate large-fixture command in the final README and rehearse every command before submission.
- **Console walkthrough for the README.** From the repo root with the host running, load a repeatable fixture using the command `docker compose run --rm db-init large-demo`. Seed `ACC-DEMO` with a known ZAR opening and 50,001 movements in July 2024. In another terminal run `dotnet run --project src/CashMovements.StatementClient -- --endpoint https://localhost:5001 --account ACC-DEMO --from 2024-07-01T00:00:00Z --to 2024-08-01T00:00:00Z --output july.csv`. Document local TLS setup, the fixture's expected opening and closing, output columns, empty ranges, Ctrl+C, exit status and partial filenames. Rehearse the example against the final host settings.
- **Demo cross-check.** Seed a June deposit of R100.00, then 25,000 July pairs of −R1.00 and +R1.00 with distinct references, followed by +R0.50. The July CSV should report an opening of R100.00, a first running balance of R99.00 and a closing of R100.50.
- **SOLUTION and AI record.** Carry approved assumptions, error precedence, duplicate race, streaming lifetime and actual trade-offs into `SOLUTION.md`. Keep the prompts, assistant suggestions, candidate decisions and verification commands/results in `PLANNING.md`. Label historical prompt excerpts and distinguish source checks, planned tests and executed tests. Record deviations discovered during implementation.

## 8. Implementation sequence and verification

1. **Establish the approved project structure and contract.** Create the four service projects, test projects and versioned protobuf. Verify project-reference direction and compile under the .NET 8 SDK.
2. **Build storage and seed the database.** Apply bootstrap, generated schema and seed scripts to an empty container. Run setup twice, inspect keys and constraints, and compare the migration-built schema with the scripted setup.
3. **Deliver submit and balance end to end.** Exercise a successful movement, equivalent retry, conflict and typed errors through the host. Use independent SQL connections and a start gate for concurrent submissions. Assert both results and persisted state.
4. **Deliver statement export.** Verify the numerical example and backdated-write scenario below, then exercise the real gRPC stream against the large fixture. Check cancellation and cleanup before declaring streaming complete.
5. **Add the console extension.** Once steps 1–4 pass, build the client against the same protobuf. Verify its CSV contents, progress and file-completion behaviour against the working service.
6. **Rehearse the handoff.** Follow README instructions from a fresh database, run the mandatory and selected checks, capture evidence, finalise SOLUTION and AI records, and record the narrated demo. Deliver one GitHub repository with source, scripts, tests and documents, plus an accessible video link.

**Focused proofs**

| Test | Expected evidence |
|---|---|
| Concurrent duplicate and conflict | Race equivalent requests with separate contexts and mixed-case keys. Use a test-only barrier immediately before the competing inserts to exercise the database constraint deterministically. Expect one `Recorded`, all others `AlreadyRecorded`, one row and one balance effect. Race otherwise valid different amounts under the same key. Either may win, the other gets `Conflict`, and the persisted fields and balance match the winner. Reusing the reference on another account succeeds. |
| Arithmetic and validation | Known empty account returns zero, unknown account returns `AccountNotFound`. Exercise deposits, withdrawals, a negative balance, zero rejection and all three supported minor units. Sum two valid movements to more than the movement column can hold and verify exact balances. Test the SQL numeric projection with synthetic positive and negative sums just below and at 10²⁴ in magnitude, including an empty sum. For the .NET running calculation, start at ±(10²⁴ − 0.001) BHD and add ±0.001 in the same direction. Both cases must yield `OUT_OF_RANGE` before emitting the invalid row. Exercise a value requiring rounding at the precision check and confirm it fails. |
| Business equivalence | Change one field at a time on a retry. Equivalent decimal spellings, currency casing and offset representations return `AlreadyRecorded`. An amount change, a different supported currency, a one-tick change or changed narration (including case or trailing space) yields `Conflict`. Test sub-tick input before conversion, canonical IDs, malformed input, and typed gRPC outcomes. Use the two transfer-retry histories in [the planning notes](PLANNING.md#planned-sql-server-test--retrying-either-side-of-a-transfer). |
| Statement math and boundary cases | Seed +100 before the range. At the lower bound insert −30 then +5 with the same timestamp and distinct IDs. Insert +99 exactly at the upper bound. Expect one header with the account, currency, requested bounds and opening 100, then two ordered rows with balances 70 and 75. Closing is 75 on `OK`. The adjacent range opens at 75 and reaches 174. An empty period after that history returns one header, zero movements and opening = closing = 174. A never-used account gives zero for both. Unknown accounts and invalid bounds fail before the header. |
| Consistency, streaming and cancellation | Coordinate a writer after the opening sum and before the row query. Commit a backdated opening movement and an in-range movement. Verify the active export still has the original opening and rows and the next export reflects both changes. Stream 50,001+ rows through the generated client and assert order, count and final total without collecting the rows. Cancel another run early, verify enumeration and the database read terminate, and run a follow-up query. Inspect the SQL projection/index plan and sample managed heap at two dataset sizes as a diagnostic alongside the code-path check. |

**Console acceptance checks.** Run the client against the 50,001-movement fixture. Inspect progress while the file grows, then read the finished CSV with a CSV parser, one logical record at a time. Expect one opening from the RPC header, 50,001 movements and one closing, with the fixture's known balances. Round-trip a narration containing a comma, double quotes, Unicode and an embedded newline. Check a negative first movement and a supported total wider than one movement. For an empty period with earlier activity, expect column names, an opening and a matching nonzero closing, and zero movement records. If an aggregate error follows the header, expect a partial file with no closing or success announcement.

Cancel after several records, simulate a stream that fails after its final data row, and inject a local write or publication failure. In each case verify a nonzero exit, no success announcement, any file from the run clearly marked partial, and any pre-existing destination intact. Check that the final name appears only on successful publication.

## 9. Mandatory requirement checklist

| Assessment requirement | Planned implementation and acceptance evidence |
|---|---|
| ☑ C#/.NET 8 modern service using gRPC and protobuf | Built the six-project solution and exercised all three RPCs through the generated client over real HTTP/2. |
| ☑ SQL Server persistence, keys, schema and seed scripts | Verified in step 2 against a clean Docker database, including reruns with existing data and an independent EF migration comparison. |
| ☑ Six movement fields, record a movement, retrieve current balance as a sum | Verified canonical replies against all six SQL fields, signed sums, empty and unknown accounts, and totals wider than a movement. |
| ☑ Per-account uniqueness, concurrent callers and differing duplicate fields | Verified both kinds of contested inserts on distinct SQL connections, changes to each recorded field, and reference reuse across accounts. |
| ☑ Focused automated tests for concurrency and balance math | Passed against Docker SQL Server in step 3. Tests assert RPC outcomes, stored rows and balances. |
| ☑ Ordered per-account date-range statement with running totals | Verified exact boundaries, same-instant ties, empty and populated ranges, and concurrent backdated writes inside one SQL snapshot. |
| ☑ 50,000+ results without whole-result buffering at either endpoint | Checked every row in a 50,001-movement stream, inspected SQL plans, sampled managed memory, and verified early reader and snapshot disposal on cancellation. |
| ☑ Error approach, assumptions and simplifications explained | SOLUTION describes typed statuses, snapshot lifetime, aggregate limits and partial-stream failures. The integration tests exercise errors before and after the header. |
| ☐ One GitHub repository with all source, README and SOLUTION | Fresh-clone run-through including the SQL and gRPC host instructions. Verify repository contents and video link in step 6. |
| ☐ Narrated 5–10 minute working demo | Follow the timed walkthrough below and verify duration, audio, code visibility and accessible playback. |

**Scope.** Structured logging and the console CSV client are the selected optional features. The service exposes its existing statement stream. The console process owns the local file. WCF NetTcp, batch submission and server-side file export remain deferred. AI provenance is an additional reviewer request. The brief's approximate four-hour estimate is guidance.

## 10. Seven-minute narrated demo

| Time | Show and explain |
|---|---|
| 0:00–1:00 | Explain the operator workflow. Show the running SQL container, schema and seed instructions, and the .NET 8 gRPC host. |
| 1:00–2:00 | Walk through the four projects in Rider, the six protobuf fields, and where the API maps a use-case outcome. |
| 2:00–3:30 | Submit a movement, retry with different currency casing, then change the amount. Show the three outcomes and one persisted row. |
| 3:30–4:30 | Run the simultaneous-submission test, point to the unique index, then retrieve the balance and explain the signed sum. |
| 4:30–6:15 | Run the console client against the preloaded 50,001-movement account. Show live progress, the partial filename, then the completed CSV and final count. Inspect the opening, one movement with its running balance, and the closing against the fixture totals. Explain the incremental loop and the snapshot trade-off. |
| 6:15–7:00 | Show one typed error and its correlated log, the test results, the design decisions and the AI record. |
