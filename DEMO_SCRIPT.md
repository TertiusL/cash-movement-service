# Cash Movements Service · OBS recording cues

The candidate’s recording is in the repository root. The times below were rehearsal cues for an **8:10** edit. The saved video runs **10:00.083**. [Assessment.mp4](Assessment.mp4)

## Off-camera checklist

- [ ] From the repository root, follow the [README setup](README.md#2-start-and-seed-sql-server). Start Docker Desktop, run `docker compose run --rm db-init`, then start the `CashMovements.Grpc` Debug profile in Rider. Confirm `https://localhost:5001` is listening. The standard seed includes both demo accounts.
- [ ] Use **ACC-ZAR** for every manual balance check and submission. Use **ACC-DEMO** for the July statement. Seeding preserves previous takes. If you have added movements to the busy account, rehearse the July export with a different filename and note the actual count and balances. The figures in segment 6 describe the standard seed.
- [ ] In PowerShell at the repo root, prepare a fresh reference and output path for **this take**. Keep this terminal open through the export in segment 6. Run this block again for a retake.

  ```powershell
  $take = [guid]::NewGuid().ToString('N')
  $movementRef = "OBS/$take".ToUpperInvariant()
  $csv = Join-Path 'exports' "july-$take.csv"
  $movementRef
  ```

- [ ] In Postman, use your working gRPC requests at `localhost:5001` with TLS on and the [service definition](src/CashMovements.Grpc/Protos/cashmovements/v1/cash_movements.proto) imported. Prepare the two messages below. Paste the printed reference into the submission **once**. Leave the timestamp fixed and save the first invocation for the recording.

  **GetBalance**

  ```json
  {"account_id":"ACC-ZAR"}
  ```

  **SubmitMovement**

  ```json
  {
    "movement": {
      "account_id": "ACC-ZAR",
      "external_ref": "<paste the printed reference here>",
      "currency": "ZAR",
      "amount": "125.50",
      "occurred_at": "2024-07-15T22:30:00Z",
      "narration": "Review deposit"
    }
  }
  ```

- [ ] Bookmark the code in segments 1–4. Leave the two passed cases of `MovementRpcTests.TwoInsertsOnSeparateSqlConnectionsCountOnlyTheWinner` ready for segment 3. [Test settings](README.md#tests-and-design-notes)
- [ ] Queue the three PowerShell commands from segment 6. Check the mic and text size, keep local credentials off screen, and preview the earlier closing using the two checks in segment 7.

## 1 · Follow one submission through the architecture | 0:00–0:55 · 55 sec

**On screen.** Expand `src` in [CashMovements.sln](CashMovements.sln). Open [LedgerService.SubmitAsync](src/CashMovements.UseCases/LedgerService.cs) at the call to `_movementStore.RecordAsync`. Jump to [IMovementStore](src/CashMovements.UseCases/IMovementStore.cs), then show the single registration in [Program.BuildApp](src/CashMovements.Grpc/Program.cs).

**Say.** “This service records cash movements that have already happened. Follow one deposit. The gRPC endpoint translates the request, UseCases applies the domain rules, and this call records the result. UseCases owns the store interface. Infrastructure supplies the SQL implementation, and this line in the host connects them. That’s where I used dependency inversion. The posting rules can stay the same if I change how EF stores the movement.”

**Next.** “Here’s a rule I wanted every caller to experience the same way.”

## 2 · Put each rule where it can change | 0:55–1:50 · 55 sec

**On screen.** In [LedgerService.SubmitAsync](src/CashMovements.UseCases/LedgerService.cs), point to `TryCanonicalIdentifier` and `TryReadMovementAmount`. Scroll to `GetBalanceAsync` to show the same identifier rule. Then open [BusinessRpcException.From](src/CashMovements.Grpc/BusinessRpcException.cs) at `LedgerError.Conflict`.

**Say.** “We treat `mov-123` and `MOV-123` as one reference on an account. Submission, balance lookup and statement lookup call the same identifier function. That’s one place to check what the API accepts. SQL also checks the stored form. We deliberately give up the caller’s original casing.”

“Money has its own rule. A decimal string lets the caller send `125.50` exactly. Integer minor units would also work, but every client would need the currency’s scale. I took on validation here, so a fractional cent is rejected. Now look at the error mapping. Changing the gRPC status for a conflict is an API change. Changing what counts as a conflict is a ledger change. That’s where I applied single responsibility.”

**Next.** “Two different paths through persistence have to agree on that definition of a conflict.”

## 3 · Make an ordinary retry and a racing retry mean the same thing | 1:50–3:15 · 1 min 25 sec

**On screen.** Keep [SqlMovementStore.RecordAsync and ClassifyRetry](src/CashMovements.Infrastructure/Persistence/SqlMovementStore.cs) together. Point to the two calls to `ClassifyRetry`: one after the lookup, the other after a competing insert. Show the unique account/reference index in [CashMovementsDbContext.OnModelCreating](src/CashMovements.Infrastructure/Persistence/CashMovementsDbContext.cs).

**Say.** “Imagine SQL commits my deposit and I lose the reply. I need to retry it safely. Accepting every duplicate could conceal a changed amount. Rejecting every duplicate would turn a successful retry into an error. Here I compare the incoming movement with the one that actually committed. Even narration matters because it appears on the statement.”

“Two API instances can both look up the reference and find nothing. A local lock would protect only one instance. SQL enforces the unique pair for every writer. On a unique-key error I reread the winner using a fresh context. That costs a read on the contested insert. This is where DRY matters most to me. Both paths call `ClassifyRetry`, so the answer to ‘was this the same movement?’ is the same whoever wins the race.”

**Evidence.** Briefly show the two passed cases of [MovementRpcTests.TwoInsertsOnSeparateSqlConnectionsCountOnlyTheWinner](tests/CashMovements.IntegrationTests/MovementRpcTests.cs). Point to the barrier at the insert and the single stored-row assertion. “Two connections really reached the insert. The winner’s amount explains the balance in both cases.”

**Next.** “Now follow that movement into a balance and a statement.”

## 4 · Build one consistent statement without holding the whole month | 3:15–5:10 · 1 min 55 sec

**Opening and rows.** Open [SqlMovementStore.ReadStatementAsync](src/CashMovements.Infrastructure/Persistence/SqlMovementStore.cs) at `IsolationLevel.Snapshot`. Point to `ReadBalanceAsync`, then the ordered row query. “A balance is a sum of signed movements. The current balance and statement opening use the same sum. The opening supplies a cutoff. I pay for the sum on reads and have one history to keep correct.”

**Why that snapshot matters.** “In the backdated-write test the account opens at 100 and has a debit of 30. Between the two reads, a credit of 20 is posted before the period and another 7 inside it. Separate reads could mix the old opening with the new rows and give 77. Our active statement closes at 70. A fresh one opens at 120 and closes at 97. Each report describes one point in time.” Keep the [populated snapshot test](tests/CashMovements.IntegrationTests/StatementRpcTests.cs) bookmarked if you want the two assertions on screen.

**Follow one row.** Point to `AsNoTracking()`, the timestamp/ID ordering and `AsAsyncEnumerable()`. Then show the awaited send in [CashMovementsEndpoint.ExportStatement](src/CashMovements.Grpc/CashMovementsEndpoint.cs) and the read/write loop in [StatementCsvExporter.WriteStatementAsync](src/CashMovements.StatementClient/StatementCsvExporter.cs). “The ID breaks timestamp ties. I keep a running total, send one row, and the client writes it to disk. A list would let me finish the database read sooner, but its memory would grow with the account. With streaming, a slow reader can hold the connection and old SQL row versions open longer.”

**If on time.** Point to the [recorded query plans](PLANNING.md#step-4-verification). “Even with the ordering keys, SQL chose a scan and sort on our 1,000- and 50,001-row examples. Including the four movement fields let it use an ordered seek on both. I accepted a wider index and more insert work for those reads.”

**Next.** “That's the design. I'll put one real movement through it and export a month from the busy account.”

## 5 · Prove one balance change in Postman | 5:10–6:25 · 1 min 15 sec

**On screen.** Use the two prepared Postman tabs. Keep the API code closed for the rest of the proof. The underlying RPCs are [CashMovementsEndpoint.GetBalance and SubmitMovement](src/CashMovements.Grpc/CashMovementsEndpoint.cs).

| Manual action | Point at the result |
|---|---|
| Invoke **GetBalance** for `ACC-ZAR`. | Read the actual starting balance **B** aloud. |
| Submit the fresh reference with amount `"125.50"`, then get the balance. | `SUBMISSION_DISPOSITION_RECORDED`. Read **B + 125.50 ZAR** aloud. |
| Resend the identical request, then get the balance. | `OK`, `SUBMISSION_DISPOSITION_ALREADY_RECORDED`. Same balance. |
| Change only the amount to `"126.50"`. Resend, then get the balance. | **6 · ALREADY_EXISTS**, detail **Conflict**. Same balance again. |

**Say.** “There are three submissions on screen and only one deposit in the balance. The second was safe to repeat. The third would have changed the original posting, so the service told me about the disagreement.”

**Next.** “Here's the same service serving July from the busy account.”

## 6 · Prove the full July export | 6:25–7:20 · 55 sec

**On screen.** Run the prepared command from the repository root. The client entry point is [StatementClientProgram.RunAsync](src/CashMovements.StatementClient/StatementClientProgram.cs). Use the fresh path prepared for this take.

```powershell
dotnet run --project src/CashMovements.StatementClient -- --endpoint https://localhost:5001 --account ACC-DEMO --from 2024-07-01T00:00:00Z --to 2024-08-01T00:00:00Z --output $csv
```

Point to `Completed 50001 movements` and the published filename. Run these separately. The standard seed’s narrations each occupy one line.

```powershell
Get-Content -LiteralPath $csv -TotalCount 4
Get-Content -LiteralPath $csv -Tail 2
```

**Say.** “The opening is **100.00 ZAR**. The first debit takes it to **99.00**, the second movement brings it back to **100.00**, and the final credit closes July at **100.50**. The file only gets its final name after the stream succeeds and the file closes. That lets me tell a finished statement from an interrupted download.”

**End take two here.** Hold the closing row on screen for two seconds and stop OBS. A clean cut here lets the existing closing start on its own first sentence.

## 7 · Closing for the assembled video | 7:20–8:10 · 50 sec allowance

**Use the earlier clip if it covers these points.** Play it once before editing. Look for (1) a deliberate scope decision in [SOLUTION.md](SOLUTION.md#design-decisions-agreed-during-planning) and (2) your part and the assistant’s part in the [AI assistance record](PLANNING.md#ai-assistance-record). Append the usable portion after the CSV. If a point is missing, record just that pickup.

- **Scope pickup, if needed.** “In SOLUTION.md I explain why I allow negative balances. These are reported events. Refusing a withdrawal because a deposit was entered late would hide something that happened. A transfer’s other leg is the caller’s responsibility to record and reconcile.”
- **AI pickup, if needed.** “AI helped explore alternatives, wrote implementation and tests, and ran checks. I chose the rules and reviewed each stage. For example, I asked for standard .NET decimal totals with an explicit range after reviewing a more complex proposal. I manually verified the Postman and CSV workflow. The prompts and revisions are in PLANNING.md.”

**Finish the edit.** End on the closing clip’s final sentence. Check the duration of the assembled video, including the actual length of the reused footage.

## Video sign-off

On 19 November 2026 the candidate confirmed the recording was saved and requested its publication with the repository. The MP4 contains 1920 × 1080 video and stereo audio. Its duration is **10:00.083**, 83 milliseconds over the ten-minute limit. [Final recording](Assessment.mp4) · [Workflow verification](PLANNING.md#recording-readiness)
