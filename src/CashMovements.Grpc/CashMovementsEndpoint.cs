namespace CashMovements.Grpc;

public sealed class CashMovementsEndpoint : Contracts.V1.CashMovements.CashMovementsBase
{
    private readonly LedgerService _ledger;

    public CashMovementsEndpoint(LedgerService ledger)
    {
        _ledger = ledger;
    }

    public override async Task<SubmitMovementReply> SubmitMovement(SubmitMovementRequest request, ServerCallContext context)
    {
        Movement? movement = request.Movement;
        SubmitMovementInput? command = movement is null ? null : new SubmitMovementInput(
            movement.AccountId, movement.ExternalRef, movement.Currency, movement.Amount,
            ReadUtcInstant(movement.OccurredAt),
            movement.Narration);

        LedgerResult<Submission> result = await _ledger.SubmitAsync(command, context.CancellationToken);
        if (result.Error is LedgerError error)
        {
            throw BusinessRpcException.From(error);
        }

        Submission submission = result.Value;
        return new SubmitMovementReply
        {
            Disposition = submission.Disposition switch
            {
                RecordDisposition.Recorded => SubmissionDisposition.Recorded,
                RecordDisposition.AlreadyRecorded => SubmissionDisposition.AlreadyRecorded,
                _ => throw new InvalidOperationException("Unknown submission disposition.")
            },
            Movement = ToMovement(submission.Movement)
        };
    }

    public override async Task<GetBalanceReply> GetBalance(GetBalanceRequest request, ServerCallContext context)
    {
        LedgerResult<AccountBalance> result = await _ledger.GetBalanceAsync(request.AccountId, context.CancellationToken);
        if (result.Error is LedgerError error)
        {
            throw BusinessRpcException.From(error);
        }

        AccountBalance accountBalance = result.Value;
        return new GetBalanceReply
        {
            AccountId = accountBalance.AccountId,
            Currency = accountBalance.Currency,
            Balance = CurrencyRules.Format(accountBalance.Balance, accountBalance.Currency)
        };
    }

    public override async Task ExportStatement(ExportStatementRequest request,
        IServerStreamWriter<StatementFrame> responseStream, ServerCallContext context)
    {
        IAsyncEnumerable<LedgerResult<StatementEntry>> entries = _ledger.ExportStatementAsync(
            request.AccountId, ReadUtcInstant(request.FromInclusive), ReadUtcInstant(request.ToExclusive),
            context.CancellationToken);

        await foreach (LedgerResult<StatementEntry> result in entries)
        {
            if (result.Error is LedgerError error)
            {
                throw BusinessRpcException.From(error);
            }

            StatementFrame frame = result.Value switch
            {
                StatementOpening opening => new StatementFrame
                {
                    Header = new StatementHeader
                    {
                        AccountId = opening.AccountId,
                        Currency = opening.Currency,
                        FromInclusive = Timestamp.FromDateTime(opening.FromInclusiveUtc),
                        ToExclusive = Timestamp.FromDateTime(opening.ToExclusiveUtc),
                        OpeningBalance = CurrencyRules.Format(opening.OpeningBalance, opening.Currency)
                    }
                },
                StatementMovement entry => new StatementFrame
                {
                    Row = new StatementRow
                    {
                        Movement = ToMovement(entry.Movement),
                        RunningBalance = CurrencyRules.Format(entry.RunningBalance, entry.Movement.Currency)
                    }
                },
                _ => throw new InvalidOperationException("Unknown statement entry.")
            };
            await responseStream.WriteAsync(frame, context.CancellationToken);
        }
    }

    private static DateTime? ReadUtcInstant(Timestamp? timestamp)
    {
        return timestamp is null ? null : MovementRules.ReadUtcInstant(timestamp.Seconds, timestamp.Nanos);
    }

    private static Movement ToMovement(RecordedMovement recordedMovement)
    {
        return new Movement
        {
            AccountId = recordedMovement.AccountId,
            ExternalRef = recordedMovement.ExternalRef,
            Currency = recordedMovement.Currency,
            Amount = CurrencyRules.Format(recordedMovement.Amount, recordedMovement.Currency),
            OccurredAt = Timestamp.FromDateTime(recordedMovement.OccurredAtUtc),
            Narration = recordedMovement.Narration
        };
    }
}
