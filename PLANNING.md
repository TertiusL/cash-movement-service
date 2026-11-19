# Planning notes

## AI assistance record

The candidate set the scope, revised the design and approved the implementation milestones. The AI assistant proposed alternatives, wrote the implementation and tests for the approved stages, and ran the checks reported below. Blockquotes are selected verbatim excerpts of the candidate's original messages. The decision table and milestone notes are summaries. Verification tables record observed results. The earlier lists headed *Planned checks* describe intentions at the time. [Approved implementation plan](IMPLEMENTATION_PLAN.md)

| Stage | Where to find the prompt, decision and evidence |
|---|---|
| Planning | [Decision history](#decision-history), [original prompt excerpts](#prompts-from-the-candidate) and [changes requested before coding](#plan-revisions-before-implementation) |
| Steps 1–2 | [SQL Server in Docker, schema, seed reruns and source inspection](#foundation-verification) |
| Step 3 | [Movement submission, balances and the two-connection test](#step-3-verification) · [Amount transport and coding-convention review](#step-3-review-follow-up) |
| Step 4 | [Snapshot streaming, backdated writes and the measured index change](#step-4-verification) |
| Step 5 | [CSV publication, cancellation and the exit-code review](#step-5-verification) |
| Handoff | [Clean-checkout rehearsal](#final-handoff-rehearsal) · [Busy account in the default seed](#busy-account-in-the-default-seed) · [Manual verification and recording readiness](#recording-readiness) |

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

### Plan revisions before implementation

> Remove the proposed BigInteger/text-based aggregate machinery.

> Label recording already-occurring movements as a documented interpretation supporting our negative-balance and transfer decisions.

**AI proposal and candidate revision (summary).** The earlier plan proposed extra machinery for large totals. The candidate chose standard C# `decimal` arithmetic with an explicit supported aggregate range and errors on overflow or precision loss. In the same review, the candidate requested a header with the opening balance even for an empty period, and asked that ZAR, JPY and BHD be identified as implementation choices. The candidate approved the revised plan before coding began. The resulting precision-limit and empty-period tests are recorded under [movement and balance verification](#step-3-verification) and [statement verification](#step-4-verification).

## Verification record

During planning the assistant read the three-page assessment and checked the documented precision of protobuf timestamps, .NET ticks and SQL Server timestamps stored as `datetime2(7)`. The references are in [SOLUTION.md](SOLUTION.md#agreed-field-comparison). At that point service verification was still planned.

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

> Begin with implementation steps 1 and 2: solution structure, project dependencies, protobuf contract, SQL Server container setup, EF Core mappings/migrations, and schema/seed scripts.

> Build the solution and verify database setup works from a clean database and can be rerun without damaging existing data. Record what was actually verified.

The assistant created the four service projects, two test project shells, versioned protobuf, EF Core model and migration, Docker setup, SQL scripts and review instructions. The SQL setup verification runs independently of the later service tests.

**Candidate review (summary).** When Rider showed the projects and their dependencies, the candidate asked to see the exact solution path, handwritten C# and protobuf files, and how the projects included them before any Git work. The candidate then approved the foundation. For later milestones the candidate requested a clickable file-by-file guide in request order, with tests and documentation listed separately.

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

> Implement movement submission, including validation, identifier/currency normalisation, account checks and duplicate handling.

> Invalid submissions do not create movements or consume an unused reference.

**Candidate direction and AI implementation (summary).** On 18 November 2026 the candidate requested SQL Server evidence for recording, concurrent retries, conflicts and invalid submissions, followed by a file-by-file review. The approved step also included `GetBalance`. The assistant implemented both unary operations, their typed errors, logging and tests. The candidate's agreed business rules determined validation and retry precedence.

The assistant suggested a test-only EF command interceptor to pause requests immediately before their competing inserts. At that point both callers have finished their lookups. The tests assert that the two SQL connection IDs differ and check the resulting row using an independent SQL query. A separate probe supplies synthetic large numeric inputs to the production balance projection to reach the aggregate limits with a small fixture.

| Executed check | Observed result |
|---|---|
| Build and final suite | Release build on .NET SDK 8.0.428. Ran `dotnet test CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false` against SQL Server 2022 Developer in a disposable Compose project. All 50 unit and 35 integration cases passed, with zero skips. Integration calls used the generated client and real Kestrel HTTP/2 on loopback. The fixtures used freshly migrated databases. Compose removed the disposable container, network and volume. |
| Valid movement and equivalent retry | The first submission returned `Recorded`. A retry with equivalent decimal spelling, identifier/currency casing and UTC instant returned `AlreadyRecorded`. All six canonical fields matched the SQL row, and the balance was ZAR 125.50. |
| Simultaneous requests | The equal-payload race returned one `Recorded` and one `AlreadyRecorded`. The changed-amount race returned one `Recorded` and one typed `Conflict`. In both cases exactly one row existed, its fields and the balance matched the winning request, and retrying the winner succeeded. A separate unrelated SQL key violation with no winning movement surfaced as a safe server failure. |
| Conflicts and invalid input | Separate retries changed amount, supported currency, one UTC tick, narration case and trailing space. Each returned `ALREADY_EXISTS` with `CONFLICT` and preserved the original row and balance. Invalid submissions and account lookup failures left no movement. A corrected submission could use the reference. A malformed amount on a used key still returned `InvalidAmount`. |
| Current balance and transfer retries | Verified empty, negative and zero balances for ZAR, JPY and BHD, including fractional minor-unit rejections. Two maximum BHD movements produced exactly 1999999999999999.998. Synthetic wide SQL sums for both signs passed immediately inside 10²⁴ and returned typed `OUT_OF_RANGE` at the limit. Both independent transfer retry histories produced one debit and one credit, with balances of −200 and +200 on their respective accounts. |
| Error mapping and logs | A real SQL command timeout returned `UNAVAILABLE`. An injected unexpected provider failure returned a safe `INTERNAL` response and one correlated diagnostic error. Client cancellation reached the EF command token, logged at debug level and allowed the next call to succeed. A normal recording logged its operation, outcome and trace and omitted the submitted amount and narration. |

The current [README](README.md#4-try-a-request) shows both calls. At this milestone, the automated client exercised both RPCs. The optional manual `grpcurl` examples were not executed. Statement reads and running arithmetic were scheduled for step 4.

### Step 3 review follow-up

> We deliberately represent amounts as decimal strings in protobuf because protobuf has no built-in decimal scalar.

> Read AGENTS.md and apply its coding conventions to the current handwritten implementation.

> Preserve behaviour and contracts. Build and run the relevant existing tests, then provide the file-by-file review handoff. Do not commit or push yet.

The candidate confirmed the existing amount representation and asked to record its alternatives and trade-off. The assistant documented that decision and traced a submission from the wire string through its single conversion to C# `decimal` and the SQL movement column. The [amount transport decision](SOLUTION.md#agreed-amount-transport) includes examples for ZAR, JPY and BHD.

The repository had no `AGENTS.md` before this review. The assistant added the supplied rules at the root, split grouped top-level types, centralised explicit namespace imports by project and updated handwritten production and test members. A Roslyn syntax check covered all 35 handwritten C# files in six projects. It found no remaining violations in the checked type placement, member bodies, explicit local types or import rules.

On 18 November 2026, `dotnet build CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false` completed with zero warnings and errors. The existing Release suite passed all 50 unit and 35 integration cases against an isolated Docker SQL Server, with zero skips. EF's `migrations has-pending-model-changes` check confirmed that the revised mappings match the existing model snapshot.

The existing `tools/Verify-DatabaseSetup.ps1 -Port 14533` verifier also passed. It compared the scripted schema with an independent EF migration, checked database constraints and confirmed that both the base seed and the 50,001-entry July fixture preserved existing data on rerun. Both Docker verification runs removed their disposable containers, networks and volumes.

**Candidate approval (summary).** The candidate approved the implementation and coding-convention changes after review and authorised the commit and push. The assistant's check results above apply to that version.

## Step 4 verification

On 18 November 2026 the candidate requested statement streaming and specified the consistency and memory requirements:

> Stream the statement header/opening balance and ordered movements from one consistent SQL snapshot. Preserve bounded memory use without buffering the entire result.

> Verify opening and running balances, date boundaries, deterministic ordering, empty periods with a nonzero opening balance, concurrent backdated writes, 50,001 movements and cancellation against SQL Server.

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

**Candidate review (summary).** The candidate approved the streaming milestone and requested a final diff check against the tested version before commit and push. The instruction for repeat testing was explicit.

> Rerun checks only where subsequent changes have invalidated the results.

## Step 5 verification

On 18 November 2026 the candidate requested the console client and set its completion condition.

> Consume the gRPC stream incrementally and write CSV without buffering the entire statement. Display progress and support cancellation.

> Keep incomplete exports clearly marked as partial; only publish the completed filename after successful stream completion and file finalisation.

The assistant linked the existing protobuf into the console project with client-only, internal generated types. This keeps one wire definition and lets the executable run without the host or a database connection string. The writer awaits each record, carries the opening or latest running balance forward, and copies the server's exact monetary strings. Progress goes to stderr after 1,000 movements or when another movement arrives at least one second after the previous report. [Client design](SOLUTION.md#design-decisions-agreed-during-planning)

| Executed check | Observed result |
|---|---|
| Build and regression | .NET SDK 8.0.428. `dotnet restore CashMovements.sln -m:1` and `dotnet build CashMovements.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false` succeeded. The seven-project build emitted zero warnings and errors. `dotnet test CashMovements.sln --no-build --no-restore -c Release -m:1` passed 50 unit and 63 integration cases with zero skips. Seven integration cases exercise the new client over Kestrel HTTP/2 against migrated SQL Server databases. |
| July executable export | Loaded the checked-in large fixture in Docker with `db-init large-demo`. Ran the Release executable against a separate host process over loopback HTTP/2, then repeated with the README's `dotnet run` commands, Rider launch profile and trusted `https://localhost:5001` endpoint. Each July CSV was 4,464,134 bytes and yielded 50 progress reports. An independent Python CSV reader checked the ordered references and balances of all 50,001 movements, one record at a time. Opening was 100.00, the first debit left 99.00, and closing was 100.50. The integration case also checked all eight fields on each row and observed the partial file grow before the final filename appeared. |
| Empty period and CSV encoding | Both executable runs produced the column header and two August summary records, each with 100.50. In a BHD test a second CSV parser round-tripped leading and trailing spaces, a comma, double quotes, Unicode and an embedded CRLF. The test checked a one-tick UTC timestamp from an explicit offset and the exact closing balance −1999999999999999.998 after two debits. |
| Actual Ctrl+C | Sent a Windows console Ctrl+C event to each isolated client console after its partial file exceeded 32 KiB. The direct executable exited 130 with 1,358 movements on disk. The HTTPS `dotnet run` client exited 130 with 1,000. Both reported the partial path, with no closing record, success message or completed filename. The count at cancellation depends on timing. A separate integration case cancelled on a progress report, then successfully exported again to the same requested filename while preserving the original partial. |
| Remote failures | An unknown account on the running service returned `NotFound`, exit 1 and an identified zero-byte partial. For a late aggregate failure, a test supplied a synthetic opening to the production SQL aggregate and used actual stored movements to exceed the limit. A second case injected a failure at snapshot commit after the last valid row. The client received `OutOfRange` and `Internal` respectively. Each partial contained the opening and one valid row, with no closing record or success announcement. |
| Local publication failure | A second writer claimed the destination while the export was in progress. After the stream succeeded, the rename failed. The competing file retained its original contents. The client exited 1, reported its partial path and withheld success, even though the partial already contained the closing record. |

Each verification run used a disposable Compose project and removed its database container, network and volume. Local CSVs, partials, logs and TRX results are under the ignored `TestResults` directory. The README gives reproducible commands with output under `exports`. [Run the HTTPS example](README.md#export-a-statement-to-csv)

**Step 5 review follow-up.** The candidate requested a readability change after reviewing the client.

> Replace the console client's literal exit codes with descriptive named constants in ClientExitCodes.cs.

**AI implementation and verification (summary).** The assistant replaced six literal returns with named constants, documented their meanings in the README and added the candidate's naming convention to AGENTS.md. The Release solution build passed with zero warnings and errors. All seven existing CSV client integration cases passed against Docker SQL Server, retaining independent assertions on the numeric outcomes. Running the built client with no arguments also confirmed process exit code 2. The candidate then approved the milestone for commit and push.

## Final handoff rehearsal

> Verify the README setup instructions from a clean checkout using an isolated database, preserving my existing local data.

On 18 November 2026 the candidate asked for an audit against the original assessment, a clean-checkout rehearsal that preserved local data, and a review before publication. The assistant read the three-page brief and compared it with the checked-in service, client, tests and documents. At that point the candidate had yet to record the video and wanted to decide what to show and why. [Mandatory requirement checklist](IMPLEMENTATION_PLAN.md#9-mandatory-requirement-checklist) · [Subsequent recording script](DEMO_SCRIPT.md)

| Rehearsal check | Observed result |
|---|---|
| Fresh checkout and environment | Cloned `master` directly from the private GitHub origin using the available authenticated account. The clean checkout contained 80 tracked files at `07d0c823d0d159fd3796803ea97dc0bcd3ca3858`, with no prior host build output. Used Windows, the installed SDK and the machine's existing NuGet cache. |
| Restore and Release build | `dotnet --version` returned 8.0.428. `dotnet tool restore` restored EF 8.0.34. `dotnet restore CashMovements.sln` and the README's Release build succeeded for all seven projects with zero warnings and zero errors. |
| Isolated database setup | Gave each rehearsal a new Compose project name, an unused loopback SQL port, a generated process-local password and a new volume. Verified `CashMovements` did not exist before setup. The base seed created four accounts and two postings, both migrations appeared in the history table, and the README's inspection query returned 12,249.5000 for `ACC-001`. Ran the optional large fixture before starting the host. |
| Automated regression | Ran the README's `dotnet test CashMovements.sln --no-build -c Release` with a TRX logger. Both full runs passed 50 unit and 63 integration cases each, with zero failures and zero skips. The integration cases use real HTTP/2 and migrated SQL Server databases, including simultaneous inserts, backdated writes, large exports and cancellation. |
| HTTPS host and client | Confirmed an existing trusted development certificate, then launched the README's Release host profile on `https://localhost:5001`. The console exported July and August in separate invocations. July produced 4,464,134 bytes and 50,001 ordered movements, with opening 100.00, first running balance 99.00 and closing 100.50. August contained only the column header and the two 100.50 summaries. |
| Restart and preservation | Followed `docker compose down` with `up -d --wait db` on the same rehearsal project. SQL still held all 50,002 `ACC-DEMO` postings including the June opening, totalling 100.50. Removed the rehearsal containers, networks and volumes by their unique project names. The user's existing database and volumes were outside these operations. |
| Schema and seed reproducibility | Ran `tools/Verify-DatabaseSetup.ps1` in its own disposable project. Both seed modes preserved existing rows on rerun. SQL enforced the constraints, an independent EF migration matched the scripted schema, and upgrading an initial ledger preserved its existing BHD posting. Regenerating the idempotent SQL to a separate file matched the checked-in script after normalising line endings. The verification clone's Git status remained clean. |

The first ad hoc PowerShell CSV check read the record-type string inside a `switch` where it needed the full parsed row. After fixing the checker, the assistant verified every reference in the first file and repeated the export and checks against another fresh database. The shipped client and automated assertions required no changes.

**Verification boundaries.** The optional manual `grpcurl` examples were not executed. The tool was absent, and automatic approval review declined downloading and executing an external binary solely for those examples. Generated-client integration tests exercised all three RPCs, and the standalone CSV utility called the running HTTPS host. The clean clone established repository availability to the authenticated account used for that rehearsal.

**Documentation review.** The README now separates the setup/host terminal from the client terminal and explains the Docker project and port used to isolate a second copy. SOLUTION records the implemented field limits, the distinction between a submit reply and a balance query, and a direct link to the AI record. The record contains selected prompt excerpts, candidate-directed decisions and revisions, assistant contributions and the outcomes of checks actually run.

**Recording plan review.** The candidate initially asked to explain the design and where DRY and SOLID influenced the code before demonstrating the running system. The assistant proposed tracing one submission through the layers and demonstrating its effects in a separate segment.

> Lets re assess the plan for the video

**Candidate revision and AI suggestion (summary).** After rereading the reviewers’ guidance, the candidate asked to reassess the emphasis on a code walkthrough. The assistant proposed a seven-minute outline pairing implementation decisions with observable results, including a segment on the measured index change. The candidate subsequently specified the live Postman requests and CSV inspection to include in the demonstration.

## Final Rider regression

On 19 November 2026 the candidate requested a full run of the final application and a hands-on walkthrough in Rider. The assistant rebuilt the working tree at `07d0c82` with the pending documentation edits. The API and standalone CLI ran on loopback HTTPS against a separate Docker SQL Server. The assistant used separate probe accounts for writes so that the candidate could start with the empty seeded ZAR account.

| Check | Observed result |
|---|---|
| Build and regression suite | .NET SDK 8.0.428. A non-incremental Release build of all seven projects finished with zero warnings and errors. EF reported no pending model changes. All 50 unit and 63 SQL integration cases passed, with zero skips. |
| Database setup | The separate database verifier passed clean creation, schema/EF comparison, constraint checks, upgrade with existing data, and idempotent base and 50,001-movement seed reruns. It removed its own containers and volume. |
| Live gRPC requests | A generated client parsed and sent the 13 request bodies prepared for Rider, substituting an isolated write account and run ID. Recording, equivalent retries, typed conflicts, currency and precision failures, reuse of an invalid submission's reference, missing accounts, date boundaries and an empty statement matched expectations. An additional 12 overlapping identical requests yielded one `Recorded`, eleven `AlreadyRecorded` and one balance effect. Retrying with visually identical but ordinally different Unicode narration yielded `Conflict` and preserved the original. The IDE's request editor was left for the candidate's interactive check. |
| Standalone CSV and cancellation | A separate CSV parser checked all eight fields of all 50,001 July movements. The statement opened at 100.00 and closed at 100.50. August had zero movements and both summaries were 100.50. A competing existing output retained its bytes. Invalid arguments exited 2, a missing account exited 1, and an actual Windows Ctrl+C exited 130 with 664 movements in the partial file and no closing record or completed filename. The cancellation count is timing-dependent. |
| Reproduced reporting limit | On a separate ZAR account, a 1.00 posting one tick before the largest UTC instant and a 2.00 posting at the largest instant produced a current balance of 3.00. The latest legal one-tick statement reported only the 1.00 movement. An end beyond the maximum tick failed timestamp validation. This is a limitation of accepting the final instant while requiring an exclusive end on every statement. |

## Busy account in the default seed

**Candidate decision (summary).** A busy account is an expected workload. The reviewer should be able to stream its statement before or after the small example immediately after setup.

**Implementation.** The assistant included the existing July fixture in the default initializer and added both independent `ExportStatement` requests to the README. The earlier `large-demo` command still works. [Initializer](db/init.sh) · [Statement examples](README.md#stream-a-statement)

**Verification.** With a fresh Docker SQL Server volume, one default initialization prepared both statements. The Release gRPC service and CSV client streamed July for `ACC-DEMO` first (50,001 movements, opening 100.00 ZAR, closing 100.50 ZAR), then 16 July for `ACC-001` (one movement, opening 12,500.00 ZAR, closing 12,249.50 ZAR).

The database verifier found five accounts and 50,004 movements immediately after the default seed. After it inserted an extra posting, it reran both setup commands and checked that the hashes of all accounts, movements and migration history stayed identical. [Database setup verifier](tools/Verify-DatabaseSetup.ps1)

## Recording readiness

On 19 November 2026 the candidate confirmed the manual workflow.

> I’ve manually verified the workflow and am happy that it works as intended.

| Item | Evidence available for the recording |
|---|---|
| Manual demonstration | The candidate chose a live starting balance on `ACC-ZAR`, a fresh reference, an identical retry and then a changed amount. The script uses the two-connection test to explain what sequential clicks cannot demonstrate. [Postman and concurrency cues](DEMO_SCRIPT.md) |
| Statement sizes | The candidate chose a two-minute Postman preview after the full-month request exhausted Postman’s memory. The assistant used the CSV client to verify that the same preview streamed 120 movements, opening and closing at 100.00 ZAR. The full July CSV was independently checked for 50,001 movements, 100.00 opening, 99.00 after the first debit and 100.50 closing. [Statement examples](README.md#stream-a-statement) |
| Test setup | The assistant ran the README’s PowerShell command `dotnet test CashMovements.sln -c Release` against Docker SQL Server. All 50 unit and 63 integration tests passed, with zero skips. The reviewer instructions also cover an IDE’s test run configuration. [Test setup](README.md#tests-and-design-notes) |
| Recording script | The assistant checked the seven segment times, file and heading links, Postman message templates and PowerShell syntax. The two CSV inspection commands were checked against the candidate’s existing July export. The 8:10 total is the planned runtime. [OBS script and sign-off](DEMO_SCRIPT.md#video-sign-off) |

**Candidate feedback and revision (summary).** On rereading the recording instructions, the candidate questioned whether the walkthrough explained enough of the reasoning. The assistant added the lost-reply and backdated-posting examples. The snapshot example distinguishes a hypothetical mixed closing of 77.00 from the observed closings of 70.00 and 97.00. The index example uses the recorded query plans to explain a design change and its write cost. [Verification evidence](#step-4-verification)

**Second take (summary).** The candidate chose to explain DRY and SOLID in the code first, demonstrate the outcomes briefly, and reuse earlier footage if it covered SOLUTION.md and AI assistance. The assistant reorganised the cues around the shared retry comparison, the store interface owned by UseCases, the API’s error mapping and the SQL snapshot. The cue sheet placed the cut after the July CSV closing balance. [Recording cues](DEMO_SCRIPT.md)

**Final recording, 19 November 2026.** The candidate confirmed the MP4 was saved and requested its publication. The assistant linked it at the start of the README and checked the file metadata. The recording has 1080p video, stereo audio and a duration of 10:00.083. Reviewer-account playback has not been independently verified. [Assessment video](Assessment.mp4)
