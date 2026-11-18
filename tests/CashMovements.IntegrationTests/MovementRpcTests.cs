namespace CashMovements.IntegrationTests;

public sealed class MovementRpcTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public MovementRpcTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    private static Movement CreateMovement(string accountId, string amount = "125.50", string currency = "ZAR")
    {
        return new Movement
        {
            AccountId = accountId, ExternalRef = "StMt/001_05", Amount = amount, Currency = currency,
            OccurredAt = Timestamp.FromDateTimeOffset(new DateTimeOffset(2024, 7, 16, 0, 30, 0, TimeSpan.FromHours(2))),
            Narration = "  Café\r\nDeposit  "
        };
    }

    private Task<SubmitMovementReply> SubmitAsync(Movement? movement)
    {
        return fixture.Client.SubmitMovementAsync(new SubmitMovementRequest { Movement = movement }).ResponseAsync;
    }

    private Task<GetBalanceReply> GetBalanceAsync(string accountId)
    {
        return fixture.Client.GetBalanceAsync(new GetBalanceRequest { AccountId = accountId }).ResponseAsync;
    }

    internal static async Task AssertRpcErrorAsync(Func<Task> invokeRpc, StatusCode expectedStatus, BusinessErrorReason expectedReason)
    {
        RpcException error = await Assert.ThrowsAsync<RpcException>(invokeRpc);
        Assert.Equal(expectedStatus, error.StatusCode);
        Metadata.Entry trailer = Assert.Single(error.Trailers, metadataEntry => metadataEntry.Key == "cash-error-bin");
        Assert.Equal(expectedReason, BusinessError.Parser.ParseFrom(trailer.ValueBytes).Reason);
    }

    [Fact]
    public async Task RecordThenRetryEquivalentRepresentationsReturnsTheCanonicalSixFields()
    {
        string accountId = await fixture.CreateAccountAsync();
        Movement movement = CreateMovement(accountId.ToLowerInvariant(), "000125.50000", "zar");
        SubmitMovementReply firstReply = await SubmitAsync(movement);
        Assert.Equal(SubmissionDisposition.Recorded, firstReply.Disposition);
        Movement canonicalMovement = CreateMovement(accountId);
        canonicalMovement.ExternalRef = "STMT/001_05";
        Assert.Equal(canonicalMovement, firstReply.Movement);

        canonicalMovement.OccurredAt = Timestamp.FromDateTime(new DateTime(2024, 7, 15, 22, 30, 0, DateTimeKind.Utc));
        canonicalMovement.Currency = "ZaR";
        SubmitMovementReply retryReply = await SubmitAsync(canonicalMovement);
        Assert.Equal(SubmissionDisposition.AlreadyRecorded, retryReply.Disposition);
        Assert.Equal(firstReply.Movement, retryReply.Movement);
        StoredMovement storedMovement = Assert.Single(await fixture.ReadRowsAsync(accountId));
        Assert.Equal(new StoredMovement(storedMovement.Id, accountId, "STMT/001_05", "ZAR", 125.50m,
            canonicalMovement.OccurredAt.ToDateTime(), movement.Narration), storedMovement);
        canonicalMovement.Amount = "0";
        await AssertRpcErrorAsync(() => SubmitAsync(canonicalMovement), StatusCode.InvalidArgument, BusinessErrorReason.InvalidAmount);
        Assert.Equal(storedMovement, Assert.Single(await fixture.ReadRowsAsync(accountId)));
        Assert.Equal(new GetBalanceReply { AccountId = accountId, Currency = "ZAR", Balance = "125.50" },
            await GetBalanceAsync(accountId.ToLowerInvariant()));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("occurredAt")]
    [InlineData("narration case")]
    [InlineData("narration space")]
    public async Task ChangedRecordedFieldConflictsAndPreservesTheOriginal(string field)
    {
        string accountId = await fixture.CreateAccountAsync();
        Movement movement = CreateMovement(accountId);
        await SubmitAsync(movement);
        StoredMovement originalMovement = Assert.Single(await fixture.ReadRowsAsync(accountId));
        switch (field)
        {
            case "amount":
                movement.Amount = "125.51";
                break;
            case "currency":
                movement.Currency = "BHD";
                break;
            case "occurredAt":
                movement.OccurredAt.Nanos += 100;
                break;
            case "narration case":
                movement.Narration = movement.Narration.ToUpperInvariant();
                break;
            case "narration space":
                movement.Narration += " ";
                break;
        }
        await AssertRpcErrorAsync(() => SubmitAsync(movement), StatusCode.AlreadyExists, BusinessErrorReason.Conflict);
        Assert.Equal(originalMovement, Assert.Single(await fixture.ReadRowsAsync(accountId)));
        Assert.Equal("125.50", (await GetBalanceAsync(accountId)).Balance);
    }

    [Theory]
    [InlineData("missing", StatusCode.InvalidArgument, BusinessErrorReason.InvalidMovement)]
    [InlineData("account", StatusCode.InvalidArgument, BusinessErrorReason.InvalidIdentifier)]
    [InlineData("reference", StatusCode.InvalidArgument, BusinessErrorReason.InvalidIdentifier)]
    [InlineData("bad code", StatusCode.InvalidArgument, BusinessErrorReason.InvalidCurrency)]
    [InlineData("unsupported", StatusCode.InvalidArgument, BusinessErrorReason.UnsupportedCurrency)]
    [InlineData("mismatch", StatusCode.FailedPrecondition, BusinessErrorReason.AccountCurrencyMismatch)]
    [InlineData("zero", StatusCode.InvalidArgument, BusinessErrorReason.InvalidAmount)]
    [InlineData("sub-cent", StatusCode.InvalidArgument, BusinessErrorReason.InvalidAmount)]
    [InlineData("timestamp", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("sub-tick", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("narration", StatusCode.InvalidArgument, BusinessErrorReason.InvalidNarration)]
    [InlineData("unknown", StatusCode.NotFound, BusinessErrorReason.AccountNotFound)]
    public async Task InvalidSubmissionLeavesTheReferenceAvailable(string invalidField, StatusCode expectedStatus, BusinessErrorReason expectedReason)
    {
        string accountId = await fixture.CreateAccountAsync();
        Movement movement = CreateMovement(accountId);
        Movement? invalidMovement = movement.Clone();
        switch (invalidField)
        {
            case "missing":
                invalidMovement = null;
                break;
            case "account":
                invalidMovement.AccountId += " ";
                break;
            case "reference":
                invalidMovement.ExternalRef = "-_/";
                break;
            case "bad code":
                invalidMovement.Currency = "z r";
                break;
            case "unsupported":
                invalidMovement.Currency = "USD";
                break;
            case "mismatch":
                invalidMovement.Currency = "BHD";
                break;
            case "zero":
                invalidMovement.Amount = "-0.000";
                break;
            case "sub-cent":
                invalidMovement.Amount = "125.5000000000000000000000000000001";
                break;
            case "timestamp":
                invalidMovement.OccurredAt = null;
                break;
            case "sub-tick":
                invalidMovement.OccurredAt.Nanos = 1;
                break;
            case "narration":
                invalidMovement.Narration = "\t ";
                break;
            case "unknown":
                invalidMovement.AccountId += "-MISSING";
                break;
        }
        await AssertRpcErrorAsync(() => SubmitAsync(invalidMovement), expectedStatus, expectedReason);
        Assert.Empty(await fixture.ReadRowsAsync(accountId));
        Assert.Equal("0.00", (await GetBalanceAsync(accountId)).Balance);
        Assert.Equal(SubmissionDisposition.Recorded, (await SubmitAsync(movement)).Disposition);
        Assert.Single(await fixture.ReadRowsAsync(accountId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoInsertsOnSeparateSqlConnectionsCountOnlyTheWinner(bool differentAmounts)
    {
        string accountId = await fixture.CreateAccountAsync();
        Movement firstMovement = CreateMovement(accountId.ToLowerInvariant());
        Movement secondMovement = CreateMovement(accountId, differentAmounts ? "-5.25" : "125.50000", "zar");
        secondMovement.ExternalRef = firstMovement.ExternalRef.ToUpperInvariant();
        ConcurrentDictionary<Guid, byte> connectionIds = new ConcurrentDictionary<Guid, byte>();
        TaskCompletionSource bothConnectionsAtInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await fixture.WithReaderProbeAsync(async (command, cancellationToken) =>
        {
            if (!command.CommandText.Contains("INSERT INTO [dbo].[Movements]", StringComparison.OrdinalIgnoreCase))
                return;
            connectionIds.TryAdd(((SqlConnection)command.Connection!).ClientConnectionId, 0);
            if (connectionIds.Count == 2)
                bothConnectionsAtInsert.TrySetResult();
            await bothConnectionsAtInsert.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }, async () =>
        {
            // Observe the two original RPCs separately so the stored amount can be matched to its winning caller.
            async Task<SubmitMovementReply?> TrySubmitAsync(Movement movement)
            {
                try
                {
                    return await SubmitAsync(movement);
                }
                catch (RpcException error) when (differentAmounts && error.StatusCode == StatusCode.AlreadyExists)
                {
                    Assert.Equal(BusinessErrorReason.Conflict,
                        BusinessError.Parser.ParseFrom(error.Trailers.Get("cash-error-bin")!.ValueBytes).Reason);
                    return null;
                }
            }
            SubmitMovementReply?[] replies = await Task.WhenAll(TrySubmitAsync(firstMovement), TrySubmitAsync(secondMovement));
            Assert.Equal(2, connectionIds.Count);
            SubmitMovementReply winningReply = Assert.Single(replies, reply => reply?.Disposition == SubmissionDisposition.Recorded)!;
            if (differentAmounts)
                Assert.Single(replies, reply => reply is null);
            else
                Assert.Single(replies, reply => reply?.Disposition == SubmissionDisposition.AlreadyRecorded);

            Movement winningMovement = replies[0] == winningReply ? firstMovement : secondMovement;
            StoredMovement storedMovement = Assert.Single(await fixture.ReadRowsAsync(accountId));
            Assert.Equal(decimal.Parse(winningMovement.Amount, CultureInfo.InvariantCulture), storedMovement.Amount);
            Assert.Equal(new StoredMovement(storedMovement.Id, accountId, "STMT/001_05", "ZAR", storedMovement.Amount,
                winningMovement.OccurredAt.ToDateTime(), winningMovement.Narration), storedMovement);
            Assert.Equal(storedMovement.Amount.ToString("F2", CultureInfo.InvariantCulture), (await GetBalanceAsync(accountId)).Balance);
            Assert.Equal(SubmissionDisposition.AlreadyRecorded, (await SubmitAsync(winningMovement)).Disposition);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EitherTransferSideCanBeRetriedAfterAnUnknownResponse(bool otherSideCommitted)
    {
        string debitAccountId = await fixture.CreateAccountAsync();
        string creditAccountId = await fixture.CreateAccountAsync();
        Movement debit = CreateMovement(debitAccountId, "-200");
        Movement credit = CreateMovement(creditAccountId, "200");
        debit.ExternalRef = credit.ExternalRef = "TRF-42";
        Assert.Equal(SubmissionDisposition.Recorded, (await SubmitAsync(debit)).Disposition);
        Assert.Equal(SubmissionDisposition.AlreadyRecorded, (await SubmitAsync(debit)).Disposition);
        Assert.Empty(await fixture.ReadRowsAsync(creditAccountId));
        Assert.Equal("0.00", (await GetBalanceAsync(creditAccountId)).Balance);

        if (otherSideCommitted)
            await SubmitAsync(credit); // Simulate losing the response after the database committed it.
        Assert.Equal(otherSideCommitted ? SubmissionDisposition.AlreadyRecorded : SubmissionDisposition.Recorded,
            (await SubmitAsync(credit)).Disposition);
        Assert.Equal(SubmissionDisposition.AlreadyRecorded, (await SubmitAsync(credit)).Disposition);
        credit.Amount = "201";
        await AssertRpcErrorAsync(() => SubmitAsync(credit), StatusCode.AlreadyExists, BusinessErrorReason.Conflict);
        Assert.Equal(-200m, Assert.Single(await fixture.ReadRowsAsync(debitAccountId)).Amount);
        Assert.Equal(200m, Assert.Single(await fixture.ReadRowsAsync(creditAccountId)).Amount);
        Assert.Equal("-200.00", (await GetBalanceAsync(debitAccountId)).Balance);
        Assert.Equal("200.00", (await GetBalanceAsync(creditAccountId)).Balance);
    }

    [Fact]
    public async Task ADuplicateSqlErrorWithoutAWinnerNeverReportsASuccessfulRetry()
    {
        string accountId = await fixture.CreateAccountAsync();
        Movement movement = CreateMovement(accountId);
        await fixture.WithReaderProbeAsync((command, _) =>
        {
            if (command.CommandText.Contains("INSERT INTO [dbo].[Movements]", StringComparison.OrdinalIgnoreCase))
            {
                // A real 2627 on an unrelated key with no winner under the submitted account/reference.
                command.CommandText = """
                    DECLARE @Other TABLE (Id int PRIMARY KEY);
                    INSERT INTO @Other (Id) VALUES (1), (1);
                    """;
            }
            return Task.CompletedTask;
        }, async () =>
        {
            RpcException error = await Assert.ThrowsAsync<RpcException>(() => SubmitAsync(movement));
            Assert.Equal(StatusCode.Internal, error.StatusCode);
        });
        Assert.Empty(await fixture.ReadRowsAsync(accountId));
        Assert.Equal(SubmissionDisposition.Recorded, (await SubmitAsync(movement)).Disposition);
    }

    [Theory]
    [InlineData("ZAR", "-0.01", "-0.001", "0.00")]
    [InlineData("JPY", "-1", "-0.1", "0")]
    [InlineData("BHD", "-0.001", "-0.0001", "0.000")]
    public async Task SignedBalanceAndCurrencyMinorUnitsAreExact(string currency, string debitAmount, string invalidAmount, string zeroBalance)
    {
        string accountId = await fixture.CreateAccountAsync(currency);
        Movement movement = CreateMovement(accountId, invalidAmount, currency);
        Assert.Equal(zeroBalance, (await GetBalanceAsync(accountId)).Balance);
        await AssertRpcErrorAsync(() => SubmitAsync(movement), StatusCode.InvalidArgument, BusinessErrorReason.InvalidAmount);
        movement.Amount = debitAmount;
        Assert.Equal(SubmissionDisposition.Recorded, (await SubmitAsync(movement)).Disposition);
        Assert.Equal(debitAmount, (await GetBalanceAsync(accountId)).Balance);
        movement.ExternalRef = "CREDIT";
        movement.Amount = debitAmount[1..];
        await SubmitAsync(movement);
        Assert.Equal(zeroBalance, (await GetBalanceAsync(accountId)).Balance);
        Assert.Equal(0m, (await fixture.ReadRowsAsync(accountId)).Sum(storedMovement => storedMovement.Amount));
    }

    [Fact]
    public async Task BalanceCanExceedASingleMovementAndMissingAccountsHaveATypedError()
    {
        string accountId = await fixture.CreateAccountAsync("BHD");
        Movement movement = CreateMovement(accountId, "999999999999999.999", "BHD");
        await SubmitAsync(movement);
        movement.ExternalRef = "SECOND";
        await SubmitAsync(movement);
        Assert.Equal("1999999999999999.998", (await GetBalanceAsync(accountId)).Balance);
        await AssertRpcErrorAsync(() => GetBalanceAsync(accountId + "-MISSING"), StatusCode.NotFound, BusinessErrorReason.AccountNotFound);
        await AssertRpcErrorAsync(() => GetBalanceAsync("BAD ACCOUNT"), StatusCode.InvalidArgument, BusinessErrorReason.InvalidIdentifier);
    }
}
