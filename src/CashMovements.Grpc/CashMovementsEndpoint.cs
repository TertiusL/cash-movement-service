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
            movement.OccurredAt is Timestamp timestamp ? MovementRules.ReadUtcInstant(timestamp.Seconds, timestamp.Nanos) : null,
            movement.Narration);

        LedgerResult<Submission> result = await _ledger.SubmitAsync(command, context.CancellationToken);
        if (result.Error is LedgerError error)
        {
            throw BusinessRpcException.From(error);
        }

        Submission submission = result.Value;
        RecordedMovement recordedMovement = submission.Movement;
        return new SubmitMovementReply
        {
            Disposition = submission.Disposition switch
            {
                RecordDisposition.Recorded => SubmissionDisposition.Recorded,
                RecordDisposition.AlreadyRecorded => SubmissionDisposition.AlreadyRecorded,
                _ => throw new InvalidOperationException("Unknown submission disposition.")
            },
            Movement = new Movement
            {
                AccountId = recordedMovement.AccountId,
                ExternalRef = recordedMovement.ExternalRef,
                Currency = recordedMovement.Currency,
                Amount = CurrencyRules.Format(recordedMovement.Amount, recordedMovement.Currency),
                OccurredAt = Timestamp.FromDateTime(recordedMovement.OccurredAtUtc),
                Narration = recordedMovement.Narration
            }
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
}
