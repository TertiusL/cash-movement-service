# Planning notes

The [approved implementation plan](IMPLEMENTATION_PLAN.md) consolidates the agreed rules, approved defaults, build sequence and assessment checklist. This file retains the decision and verification history.

## Decision history

| Topic | Origin and status | Reason |
|---|---|---|
| Account currency | Candidate's initial decision | A fixed currency per account gives every balance one unit. |
| Amount transport | **Agreed.** The candidate chose invariant decimal strings in protobuf because it has no built-in decimal scalar. The service validates and parses a submitted amount once at entry. Business calculations use C# `decimal` and stored movements use SQL `decimal(19,4)`. The money path excludes `float` and `double`. | The wire value is readable and exact with a single field. The application must enforce syntax, range and currency precision and reject values that would require rounding. We considered coefficient plus scale and integer minor units. Both represent money exactly but require representation rules and additional conversion logic. [Decision and worked example](SOLUTION.md#agreed-amount-transport) |
| Account existence | **Agreed.** The candidate confirmed that movements require an existing account, that unknown accounts return `AccountNotFound`, and that seed data should provide usable accounts. | A typo must not create an account or a posting. The trade-off is that provisioning precedes the first movement. A known empty account can already be queried in its own currency. |
| Movement identity | The brief requires uniqueness per account, including concurrent submissions. The assistant suggested making the first committed movement authoritative and treating changed narration as a conflict. The candidate agreed and explicitly included amount, currency, occurrence time and narration. | A caller can safely retry after losing a response. A conflicting submission cannot change the ledger or give the impression that a correction was recorded. |
| Business outcomes | Candidate requested transport-independent outcomes in `UseCases`, with a preference for enums, central API error mapping and shared exception handling. | Clients can act on stable codes. Every endpoint follows one policy using in-process components. |
| Structured logs | Candidate selected this optional feature and specified correlation, appropriate levels, privacy, one-time exception logging and separate handling of client cancellation. | Operators can trace a call and diagnose failures while limiting sensitive data and noise. |
| Console CSV client | **Selected optional extension.** The candidate requested a local, incremental CSV export with progress, cancellation and explicit partial-file handling after the required service works. | The console owns file creation and publication. The [client design](IMPLEMENTATION_PLAN.md#console-client-and-local-csv) uses the agreed statement header for the opening and successful completion for the closing, including empty periods. |
| UTC and timezone responsibility | **Agreed.** Movement timestamps, storage, comparisons, responses and both statement boundaries use UTC. Equivalent instants compare equal across offsets. Callers resolve local times and reporting boundaries before submission and handle local display. | Currency and server settings cannot identify the caller's intended timezone. Regional or account timezone management, daylight-saving rules and local-calendar reporting are outside this service's scope. |
| Timestamp precision and narration | **Agreed.** The candidate approved 100-nanosecond UTC representation and ordinal comparison of preserved narration. | Reject sub-tick values that would lose precision. A changed accepted narration causes a conflict. See [SOLUTION.md](SOLUTION.md#agreed-field-comparison). |
| External reference casing | **Agreed.** The assistant initially suggested exact case. The candidate identified the operator re-entry risk and selected case-insensitive matching within each account. | The brief leaves casing unspecified. A change in capitalisation alone must not allow a second posting, even under concurrent submissions. Different accounts may reuse the same reference. |
| Account IDs and external references | **Agreed.** The candidate expanded the assistant's proposed alphabet to ASCII letters, digits, hyphens, underscores and forward slashes. Each ID or reference must contain at least one letter or digit. Reject whitespace anywhere. Use uppercase for storage, output and lookup, preserving punctuation and leading zeros. | The brief leaves format and casing open. One spelling prevents casing-only duplicate movements. Both `STMT/1004/08` and `STMT_1004/08` are valid and distinct. Original casing is lost, and upstream identifiers that rely on case or contain unsupported characters cannot be used as issued. Application validation, lookups and database constraints follow the [agreed identifier format](SOLUTION.md#agreed-identifier-format). |
| Amount precision | **Agreed.** The candidate suggested four decimal places and confirmed `decimal(19,4)` storage with exact minor-unit validation before posting. | One column supports multiple currency scales. Rejecting fractional minor units keeps the amount supplied by the caller and the amount entered in the ledger consistent. |
| Supported currencies | **Agreed implementation choice.** The brief gives ZAR as an example. The candidate chose ZAR (two decimal places), JPY (zero) and BHD (three) as our supported set. | These three currencies demonstrate that validation follows each account's currency. For example, `125.001` is valid on a BHD account and invalid on a ZAR or JPY account. |
| Currency-code spelling | **Agreed.** Require exactly three ASCII letters with no whitespace. Uppercase the input, validate it against ZAR, JPY and BHD, and use the canonical code for storage, comparisons and responses. | A caller can vary letter case on a retry without causing a conflict or a second posting. |
| Zero-value movements | **Agreed.** Return `InvalidAmount` for zero and leave an unused reference available. A valid withdrawal can bring a balance to zero. | The candidate identified memos, notes and reconciliation markers as a separate workflow. They do not justify creating a cash movement. |
| Negative balances | **Agreed.** The candidate confirmed that an otherwise valid withdrawal may produce or deepen a negative balance. | Our interpretation is that operators record events that have already occurred. Under this assumption, available-funds checks and debt handling are outside scope. |
| Transfers | **Agreed.** Under the same interpretation, record the reported debit and credit independently. The caller owns retries and reconciliation. Transfer execution and coordination are outside this service's scope. | Success on A proves only that A's movement was recorded. The committed movement for B determines B's retry outcome. Logs assist investigation. |
| Aggregate arithmetic | **Agreed.** Use standard .NET `decimal`. The agreed aggregate limit is `decimal(28,4)` for current, opening, running and closing balances. Movement storage remains `decimal(19,4)`. | Same-scale numeric SQL conversion and checked, range-validated addition keep totals exact within the documented bound. Any overflow or loss of precision yields `OUT_OF_RANGE` with `AGGREGATE_OUT_OF_RANGE`. |
| Statement header | **Agreed.** Send exactly one header with account, currency, UTC bounds and opening, followed by movement frames from the same snapshot. | On successful completion, closing equals the last running balance, or the opening if there were no movements. The CSV can report both balances even in an empty period. |

## Prompts from the candidate

Selected verbatim excerpts from the planning conversation, in order.

> I want to work through this step by step and plan then implement every design decision so I can explain and defend the solution during the technical interview.

> An initial decision is one fixed currency per account, with different accounts allowed to have different currencies. Movements must match the account currency. Document this assumption and its rationale in SOLUTION.md when we begin maintaining the project documents.

> An identical retry returns a successful “already recorded” outcome without changing the stored movement or balance.

> Changes to amount, currency, occurredAt or narration for the same account/externalRef return a conflict. The original remains unchanged.

> UseCases should expose explicit, machine-readable business outcomes(enums are shmexy), independent of gRPC.

> Do not routinely log full requests, narration, amounts or credentials.

> Before moving on, clarify how we will compare movement fields, including equivalent amounts and timestamps, and whether any string normalisation will apply.

> in my opinion mov-123 and MOV-123 is most likely the same thing entered by a user, without caps?

> I have been using 4 decimals for currency in hopes to minimise the potential rounding issues - what are your thoughts?

> Agreed. Use decimal(19,4) for storage, but reject amounts smaller than the currency’s minor unit rather than rounding. Treat mov-123 and MOV-123 as the same reference per account. Mark those as agreed.

> I propose normalising both account IDs and external references to uppercase before storage and lookup, making both identifiers case-insensitive. Return their canonical uppercase form; we don’t need to preserve the original casing.

> Account IDs and external references accept ASCII letters, digits, hyphens, underscores and forward slashes, with at least one letter or digit. Whitespace is rejected. Letters are normalised to uppercase for storage, output and lookup; punctuation and leading zeros are preserved.

> an account absolutely needs to exist for movements to succeed. if an account does not exist we should surface the 'account not found' error.

> absolutely agreed. notes, markers or otherwise memo/reconciliation matters would in a real application have a seperate system, and absolutely should not rely on 'blank' movements to record such memos or notes.

> I believe having the balance be able to go below 0 matches the given task, since we are recording already made movements.

> Logs support investigation, but persisted movements determine recording status. Success on A proves only that A’s movement was recorded, not that B or the overall transfer succeeded.

> Document that the caller is responsible for retries and reconciliation, and include a focused test showing that retrying either side cannot double-count it.

> Yes I believe those 3 cover a good example for our validation on amounts according to the accounts currency.

> Casing differences alone must not cause a duplicate conflict. Document the rule and rationale in SOLUTION.md and include a test for a retry using different currency casing.

> Equivalent instants must compare equal regardless of the original offset. Do not infer a timezone from the account currency or the server’s local timezone.

> Keep the proposed timestamp precision rule as a separate decision from timezone handling.

> Present one coherent plan for review before implementation. Do not write application code yet.

> It should request a statement through gRPC, consume entries incrementally and write them directly to a local CSV file while displaying progress. It must not accumulate the entire statement in memory.

> Ensure an empty period still exposes its opening and closing balances from the same snapshot. Update the CSV design and tests consistently.

## Verification record

During planning the assistant read the three-page assessment and checked the documented precision of protobuf timestamps, .NET ticks and SQL Server timestamps stored as `datetime2(7)`. The references are in [SOLUTION.md](SOLUTION.md#agreed-field-comparison). Service behaviour is scheduled for the later implementation stages.

Two published contracts illustrate why casing depends on the field's purpose. IATA's Simplified Invoicing and Settlement guide makes invoice numbers case-insensitive for duplicate checks. AWS documents a case-sensitive client token for idempotency on its payment operation. The IATA example more closely resembles a reference entered by an operator. [IATA SIS User Guide, Invoice Number](https://www.iata.org/globalassets/iata/services/financial-services/sis/support-docs/sis_member_user_guide.pdf) · [AWS ProcessPayment request](https://docs.aws.amazon.com/sdkfornet/v4/apidocs/items/BedrockAgentCore/TProcessPaymentRequest.html)

Planned checks for the service and client stages

- After seeding, query an account with no movements. Expect zero in its currency and one statement header with zero opening, zero movement frames and closing zero on success. Submit its first valid deposit and verify the new balance. For a well-formed unknown ID, expect `AccountNotFound` before a header and no account or movement created.
- For a period with R100 of prior activity and no in-range movements, expect the account, ZAR, requested UTC bounds and R100 opening in the first frame. On successful completion the closing is R100. Check both CSV summaries and a movement count of zero. A later deposit committed during the export cannot change either summary.
- Check exact numeric sums above a single movement's range and both signs of the agreed `decimal(28,4)` limit. Verify an unsupported opening fails before the header, and a running total that crosses the bound after the header ends the RPC without a rounded row or a completed CSV.
- Race equivalent submissions for `acc-001` and `ACC-001` using the reference spellings `stmt/1004/08` and `STMT/1004/08`. Expect one movement and one balance effect. Fetch the balance and statement with mixed-case account IDs and check the uppercase identifiers in responses. Submit the same reference to a second account and expect an independent posting.
- Exercise both identifier fields with a single digit, mixed-case letters and each allowed punctuation mark. Verify that `STMT/1004/08`, `STMT_1004/08` and `STMT/1004/8` remain distinct. Reject empty and punctuation-only values such as `-/_`, whitespace anywhere, a backslash and a full-width letter. Check that SQL constraints accept canonical values and reject invalid or lowercase values on direct writes.
- Race two otherwise valid submissions with the same key and different amounts or narrations. Either may win. Expect the other to conflict, and check the stored fields and balance against the winner.
- **UTC equivalence.** Submit a movement at the instant represented by `2024-07-16T00:30:00+02:00`. In a fresh scope, retry at `2024-07-15T22:30:00Z` with all other fields identical. Expect `AlreadyRecorded`, one stored movement and a single balance effect. Query the statement using bounds corresponding to 00:00 and 01:00 at +02:00 on July 16, then query using 22:00 and 23:00 UTC on July 15. Expect the same row in both responses, timestamped 22:30 UTC. The movement is strictly inside both ranges.
- **Precision checks.** Test that a one-tick change yields a conflict and that finer instants requiring rounding or truncation fail validation.
- Retry after reloading the original from SQL Server to check how a change in narration whitespace is classified.
- **Currency casing on retry.** Start with an empty ZAR account. Submit a deposit of 125.50 under reference `MOV-123` with currency `zAr` and expect `Recorded`. In a fresh service scope, repeat the submission changing only the currency to `ZAR`. Expect `AlreadyRecorded`. Verify one stored movement with currency `ZAR`, a statement row reporting `ZAR`, and a balance of 125.50 `ZAR`.
- Check malformed and unsupported inputs separately. An otherwise valid new submission using `" ZAR"`, `"Z R"`, `"ZA"`, `"ZA1"`, `"ＺAR"` (full-width Z) or `"USD"` must fail validation without recording a movement.
- For a ZAR account, retry a movement of `125.50` with `125.5000` and expect success. Submit `125.5001` and expect validation to reject it before any rounding. Check that the balance still includes only the original amount.
- On a JPY account, accept `125.0000` and reject `125.1`. Submit `125.001` in each account's own currency and expect acceptance only for BHD. On the BHD account, also reject `125.0001`. Verify each rejected submission leaves no movement row or balance change.
- Submit zero with a fresh reference to a known account. Expect `InvalidAmount`, the original balance and no statement row. Submit a valid deposit with that reference and verify that it records once. Withdraw the deposited amount from an otherwise empty account and verify a zero balance.
- Record a withdrawal of R60 on an existing ZAR account with no movements. Expect a balance of −R60. Enter an earlier R100 deposit and verify that both movements are present and the balance is R40.
- Verify that clients can distinguish outcomes from structured responses and status codes. Exercise an unexpected failure and a client cancellation to check correlation, logging levels, safe client errors and omission of sensitive fields.

### Planned SQL Server test — retrying either side of a transfer

Run the same scenario for two possible database states after a caller receives no response for B. Start each case with two existing ZAR accounts, A and B, both with zero balance and no movements. Submit A's debit of R200 with reference `TRF-42`, then retry A with identical details. Expect `Recorded` followed by `AlreadyRecorded`. Verify A has one debit and B still has zero movements and a zero balance.

| State before retrying B | Test setup | Expected outcome when resubmitting B's R200 credit under `TRF-42` |
|---|---|---|
| B's first attempt never committed | Leave B with no row for the reference. | `Recorded` |
| B committed and the response was lost | Submit the credit, let it commit and discard its response. | `AlreadyRecorded` |

Make the retry through a fresh service scope. In both cases, send the identical B credit once more and expect `AlreadyRecorded`. Change its amount to R201 with the same key and expect `Conflict`. Read back exactly one persisted row per account for `TRF-42`, with A's original debit of R200 and B's original credit of R200. Verify the individual account balances are −R200 and R200. A combined zero balance alone would also pass if both sides were counted twice.

The candidate approved the remaining defaults, including timestamp precision, half-open ranges, narration comparison, numeric limits and the gRPC contract. Authorisation for this checkpoint covers steps 1 and 2.

## Foundation verification

On 18 November 2026 the candidate approved the revised plan and requested implementation of the foundation with SQL Server in Docker.

> Build the solution and verify database setup works from a clean database and can be rerun without damaging existing data. Record what was actually verified.

The assistant created the four service projects, two test project shells, versioned protobuf, EF Core model and migration, Docker setup, SQL scripts and review instructions. The SQL setup verification runs independently of the later service tests.

| Check run | Observed result |
|---|---|
| SDK and project references | `dotnet --version` selected 8.0.428. `dotnet sln list` showed six projects. Domain has no project dependencies, UseCases references Domain, Infrastructure references UseCases and Domain, and Grpc references UseCases and Infrastructure. |
| Release compilation | `dotnet build CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false` built all six projects with zero warnings and zero errors. The protobuf generated two unary operations, one server-streaming operation, header/row frames and the typed error model. |
| Migration consistency | EF Core 8.0.34 `migrations has-pending-model-changes` found no changes. An independent `migrations script --idempotent` run produced a byte-identical match to `db/schema.sql`. |
| Host startup | The Release host with its launch profile listened on `https://localhost:5001` and was then stopped. This checked Kestrel startup only. The sandbox lacked access to the Windows development certificate store, so the successful run used the normal user context. |
| Clean Docker database | `tools/Verify-DatabaseSetup.ps1` exited 0 using SQL Server 2022 Developer, image `mcr.microsoft.com/mssql/server:2022-CU28-ubuntu-22.04`, digest `sha256:4525e3efac9f3e241cb003bb2ff2a82818b502f37e12049d788b1a9fa7e13b7f`. The script confirmed `CashMovements` was absent before initialisation. Bootstrap enabled snapshot isolation. Schema and seed produced one migration-history entry, four accounts and two movements on `ACC-001` totalling R12,249.50. |
| Rerun with existing data | Inserted a separate R37.25 posting on `ACC-ZAR`. After repeating base setup, ordered hashes over every account, every movement (including IDs and narration) and the migration history matched the pre-rerun hashes. |
| Storage constraints | Direct SQL inserts in the disposable database rejected a duplicate account/reference, an account-currency mismatch, a fractional-cent ZAR amount, and lowercase, trailing-space and punctuation-only references. Concurrency classification by the service is scheduled for step 3. |
| Independent schema comparison | Applied the EF migration directly to a second clean database inside the same container. The schema script and EF migration matched on table columns and precision, keys and indexes, foreign keys, check constraints and migration history. |
| Large fixture | The SQL fixture produced one R100.00 opening movement, 50,001 July movements netting R0.50, and a closing of R100.50. Repeating large-demo setup preserved the full data and migration-history hashes. This verified the data for the later streaming test. |

The verifier removed its own temporary Compose project, container, network and volume. The downloaded SQL Server image remains available in Docker Desktop. The four service projects and two test shells follow the approved dependency layout. EF also created its conventional index on the composite foreign key. Business RPC behaviour, concurrent submissions, streamed exports and the console client are reserved for subsequent stages.

## Step 3 verification

On 18 November 2026 the candidate asked for movement submission and SQL Server evidence, followed by a file-by-file review. The instruction “Complete any other work explicitly included in step 3, without moving into later steps” also covers `GetBalance`. The assistant implemented both unary operations, their error and logging policy, and the tests. The candidate's agreed business rules determined validation and retry precedence.

The assistant suggested a test-only EF command interceptor to pause requests immediately before their competing inserts. At that point both callers have finished their lookups. The tests assert that the two SQL connection IDs differ and check the resulting row using an independent SQL query. A separate probe supplies synthetic large numeric inputs to the production balance projection to reach the aggregate limits with a small fixture.

| Executed check | Observed result |
|---|---|
| Build and final suite | Release build on .NET SDK 8.0.428. Ran `dotnet test CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false` against SQL Server 2022 Developer in a disposable Compose project. All 50 unit and 35 integration cases passed, with zero skips. Integration calls used the generated client and real Kestrel HTTP/2 on loopback. The fixtures used freshly migrated databases. Compose removed the disposable container, network and volume. |
| Valid movement and equivalent retry | The first submission returned `Recorded`. A retry with equivalent decimal spelling, identifier/currency casing and UTC instant returned `AlreadyRecorded`. All six canonical fields matched the SQL row, and the balance was ZAR 125.50. |
| Simultaneous requests | The equal-payload race returned one `Recorded` and one `AlreadyRecorded`. The changed-amount race returned one `Recorded` and one typed `Conflict`. In both cases exactly one row existed, its fields and the balance matched the winning request, and retrying the winner succeeded. A separate unrelated SQL key violation with no winning movement surfaced as a safe server failure. |
| Conflicts and invalid input | Separate retries changed amount, supported currency, one UTC tick, narration case and trailing space. Each returned `ALREADY_EXISTS` with `CONFLICT` and preserved the original row and balance. Invalid submissions and account lookup failures left no movement. A corrected submission could use the reference. A malformed amount on a used key still returned `InvalidAmount`. |
| Current balance and transfer retries | Verified empty, negative and zero balances for ZAR, JPY and BHD, including fractional minor-unit rejections. Two maximum BHD movements produced exactly 1999999999999999.998. Synthetic wide SQL sums for both signs passed immediately inside 10²⁴ and returned typed `OUT_OF_RANGE` at the limit. Both independent transfer retry histories produced one debit and one credit, with balances of −200 and +200 on their respective accounts. |
| Error mapping and logs | A real SQL command timeout returned `UNAVAILABLE`. An injected unexpected provider failure returned a safe `INTERNAL` response and one correlated diagnostic error. Client cancellation reached the EF command token, logged at debug level and allowed the next call to succeed. A normal recording logged its operation, outcome and trace and omitted the submitted amount and narration. |

Reproduction instructions are in the [README](README.md#run-and-test-submission-and-balance). The optional manual `grpcurl` examples were not executed in this environment. The automated client exercised the same two RPCs. Statement reads and running arithmetic remain scheduled for step 4.

### Step 3 review follow-up

> We deliberately represent amounts as decimal strings in protobuf because protobuf has no built-in decimal scalar.

> Read AGENTS.md and apply its coding conventions to the current handwritten implementation.

The candidate confirmed the existing amount representation and asked to record its alternatives and trade-off. The assistant documented that decision and traced a submission from the wire string through its single conversion to C# `decimal` and the SQL movement column. The [amount transport decision](SOLUTION.md#agreed-amount-transport) includes examples for ZAR, JPY and BHD.

The repository had no `AGENTS.md` before this review. The assistant added the supplied rules at the root, split grouped top-level types, centralised explicit namespace imports by project and updated handwritten production and test members. A Roslyn syntax check covered all 35 handwritten C# files in six projects. It found no remaining violations in the checked type placement, member bodies, explicit local types or import rules.

On 18 November 2026, `dotnet build CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false` completed with zero warnings and errors. The existing Release suite passed all 50 unit and 35 integration cases against an isolated Docker SQL Server, with zero skips. EF's `migrations has-pending-model-changes` check confirmed that the revised mappings match the existing model snapshot.

The existing `tools/Verify-DatabaseSetup.ps1 -Port 14533` verifier also passed. It compared the scripted schema with an independent EF migration, checked database constraints and confirmed that both the base seed and the 50,001-entry July fixture preserved existing data on rerun. Both Docker verification runs removed their disposable containers, networks and volumes.

## Step 4 verification

On 18 November 2026 the candidate requested statement streaming and specified the consistency and memory requirements:

> Stream the statement header/opening balance and ordered movements from one consistent SQL snapshot. Preserve bounded memory use without buffering the entire result.

The assistant implemented the existing server-streaming contract and used the generated client to verify it over loopback HTTP/2 against Docker SQL Server 2022 Developer. The client consumed one frame at a time. The selected console CSV client is step 5.

| Executed check | Observed result |
|---|---|
| Build and full regression suite | .NET SDK 8.0.428, Release build with zero warnings and zero errors. All 50 unit and 56 integration tests passed with zero skips, including 21 statement cases. The isolated Compose project and its volume were removed after each run. |
| Opening, running and closing balances | An opening of 100.00 and two lower-bound movements of −30.00 and +5.00 produced 70.00 then 75.00. The movement of +99.00 exactly at the upper bound appeared in the next period, which opened at 75.00 and closed at 174.00. A later empty period returned a single header with opening and closing 174.00. A never-used JPY account returned a single header with zero. |
| Boundaries and order | The lower bound was one 100 ns tick past midnight. The opening included a posting one tick earlier. A one-tick adjacent period included exactly its lower-bound posting. Equal timestamps followed movement ID even with references in reverse lexical order. The latest event had been inserted first. Full frames matched the expected canonical movement, timestamp and narration. A second account’s movements stayed outside the result. |
| Concurrent backdated writes | The test paused the range query after the opening and confirmed both SQL commands shared the same `Snapshot` transaction object. Two independent submissions committed +20 before the lower bound and +7 within the period. The active populated export still opened at 100.00 and closed at 70.00. A new export opened at 120.00 and closed at 97.00. In the empty-period variant the active export closed at 100.00 with zero rows, and a new export closed at 127.00. |
| Before- and after-header errors | Invalid IDs, timestamps, bounds and unknown accounts produced their typed errors before the first frame. Synthetic positive and negative SQL opening sums exercised the aggregate limits. An unreportable opening failed before the header. Totals crossing the limit after a valid row ended the stream with `OUT_OF_RANGE` before the offending row. An injected row-query exception after a header returned safe `INTERNAL` details with one correlated server diagnostic. |
| Large stream | Loaded the checked-in SQL fixture. Verified every reference, timestamp, amount and running balance for 1,000 and 50,001 movements. The full July stream opened at 100.00, started with a running balance of 99.00, and closed at 100.50 on terminal gRPC `OK`. One ordered row query served each export. |
| Cancellation and cleanup | Cancelled a 50,001-movement export after receiving eight movements. In the final run EF disposed its active reader after 267 reads. The transaction detached, the SQL connection closed, the client received `CANCELLED`, and the boundary logged cancellation at debug level. A subsequent balance request succeeded at 100.50. The read count can vary with transport buffering. |
| Database upgrade | EF reported no pending model changes. The database verifier passed clean setup, independent EF/schema comparison, SQL constraints, seed reruns and an upgrade from the initial migration. The upgraded database retained its pre-existing BHD account and posting, including identity and all fields. |

**Index decision.** The first attempt at an estimated-plan diagnostic returned no plan for the parameterised call. The assistant changed the diagnostic to replay the same SQL and parameters on a separate connection with `SET STATISTICS XML ON` after the real gRPC stream finished. Actual plans for both sizes showed a clustered scan on `PK_Movements` and a sort. The assistant added the four projected payload columns to the existing ordered index in a new migration. Both final plans consisted of an `Index Seek` on `IX_Movements_AccountId_OccurredAtUtc_MovementId`. These observations apply to the tested fixture and parameters.

**Managed memory.** The final full-suite run sampled live managed bytes with `GC.GetTotalMemory(true)`. The gRPC host and client shared a process with the test runner. Other tests, collection and runtime warm-up affect these measurements. The implementation evidence for bounded memory is the single untracked projection, incremental SQL enumeration, an awaited write per frame and the client's incremental read loop.

| Movements | Before call | At midpoint | After completion |
|---|---:|---:|---:|
| 1,000 | 5,197,288 | 5,345,184 | 1,461,376 |
| 50,001 | 1,436,160 | 1,142,312 | 1,128,944 |

**Commands run.** Built with `dotnet build CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false`. Ran `dotnet test CashMovements.sln --no-build --no-restore -c Release -m:1` with detailed console and TRX loggers, `dotnet ef migrations has-pending-model-changes` against Infrastructure, and `.\tools\Verify-DatabaseSetup.ps1 -Port 14533`. The README includes a shorter Rider/PowerShell review path. Its optional manual `grpcurl` example was not executed. [Run the statement checks](README.md#stream-a-statement)
