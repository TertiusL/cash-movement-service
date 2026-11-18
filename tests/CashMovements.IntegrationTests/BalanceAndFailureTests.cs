namespace CashMovements.IntegrationTests;

public sealed class BalanceAndFailureTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture fixture;

    public BalanceAndFailureTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Theory]
    [InlineData("999999999999999999999999.999", false)]
    [InlineData("-999999999999999999999999.999", false)]
    [InlineData("1000000000000000000000000.000", true)]
    [InlineData("-1000000000000000000000000.000", true)]
    public async Task SqlBalanceProjectionHandlesBothAggregateLimits(string expectedBalance, bool exceedsAggregateLimit)
    {
        string accountId = await fixture.CreateAccountAsync("BHD");
        GetBalanceRequest request = new GetBalanceRequest { AccountId = accountId };
        await fixture.WithReaderProbeAsync((command, _) =>
        {
            // Supply a synthetic wide numeric input instead of creating billions of movements.
            // The production SUM, conversion, account query and gRPC mapping still execute against SQL Server.
            const string movementTableSource = "dbo.Movements AS m";
            if (command.CommandText.Contains(movementTableSource, StringComparison.Ordinal))
            {
                const string syntheticAmountSource = "(VALUES (@testAccount, CAST(@testAmount AS decimal(38,4)))) AS m(AccountId, Amount)";
                command.CommandText = command.CommandText.Replace(movementTableSource, syntheticAmountSource, StringComparison.Ordinal);
                SqlParameterCollection parameters = ((SqlCommand)command).Parameters;
                parameters.Add("@testAccount", SqlDbType.NVarChar, 64).Value = accountId;
                parameters.Add("@testAmount", SqlDbType.VarChar, 40).Value = expectedBalance;
            }
            return Task.CompletedTask;
        }, async () =>
        {
            if (exceedsAggregateLimit)
                await MovementRpcTests.AssertRpcErrorAsync(() => fixture.Client.GetBalanceAsync(request).ResponseAsync,
                    StatusCode.OutOfRange, BusinessErrorReason.AggregateOutOfRange);
            else
                Assert.Equal(expectedBalance, (await fixture.Client.GetBalanceAsync(request)).Balance);
        });
    }

    [Fact]
    public async Task UnexpectedProviderFailureReturnsASafeCorrelatedStatus()
    {
        string accountId = await fixture.CreateAccountAsync();
        const string diagnostic = "test-only confidential provider diagnostic";
        await fixture.WithReaderProbeAsync((_, _) => throw new InvalidOperationException(diagnostic), async () =>
        {
            RpcException exception = await Assert.ThrowsAsync<RpcException>(
                () => fixture.Client.GetBalanceAsync(new GetBalanceRequest { AccountId = accountId }).ResponseAsync);
            Assert.Equal(StatusCode.Internal, exception.StatusCode);
            Assert.DoesNotContain(diagnostic, exception.Status.Detail);
            string? traceId = exception.Trailers.Get("trace-id")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(traceId));
            LogEvent failureEvent = Assert.Single(fixture.Logs.Drain(), logEvent => logEvent.Exception?.Message == diagnostic);
            Assert.Equal(typeof(RpcOutcomeInterceptor).FullName, failureEvent.Category);
            Assert.Equal(LogLevel.Error, failureEvent.Level);
            Assert.Equal(traceId, failureEvent.Fields["TraceId"]);
            Assert.Equal("Internal", failureEvent.Fields["Outcome"]);
        });
    }

    [Fact]
    public async Task SqlCommandTimeoutReturnsASafeUnavailableStatus()
    {
        string accountId = await fixture.CreateAccountAsync();
        await fixture.WithReaderProbeAsync((command, _) =>
        {
            command.CommandTimeout = 1;
            command.CommandText = "WAITFOR DELAY '00:00:05'; " + command.CommandText;
            return Task.CompletedTask;
        }, async () =>
        {
            RpcException exception = await Assert.ThrowsAsync<RpcException>(
                () => fixture.Client.GetBalanceAsync(new GetBalanceRequest { AccountId = accountId }).ResponseAsync);
            Assert.Equal(StatusCode.Unavailable, exception.StatusCode);
            Assert.DoesNotContain("WAITFOR", exception.Status.Detail);
            LogEvent failureEvent = Assert.Single(fixture.Logs.Drain(), logEvent => logEvent.Exception is SqlException { Number: -2 });
            Assert.Equal(LogLevel.Warning, failureEvent.Level);
            Assert.Equal(exception.Trailers.Get("trace-id")!.Value, failureEvent.Fields["TraceId"]);
        });
    }

    [Fact]
    public async Task RoutineOutcomeLogsIncludeTheCallTraceAndOmitRecordedValues()
    {
        string accountId = await fixture.CreateAccountAsync();
        Movement movement = new Movement
        {
            AccountId = accountId, ExternalRef = "LOG-CHECK", Amount = "985431729876.07", Currency = "ZAR",
            OccurredAt = Timestamp.FromDateTime(DateTime.UnixEpoch), Narration = "test-only private narration"
        };
        using AsyncUnaryCall<SubmitMovementReply> call = fixture.Client.SubmitMovementAsync(new SubmitMovementRequest { Movement = movement });
        Assert.Equal(SubmissionDisposition.Recorded, (await call.ResponseAsync).Disposition);
        string traceId = call.GetTrailers().Get("trace-id")!.Value;
        LogEvent[] logEvents = fixture.Logs.Drain().ToArray();
        LogEvent recordedEvent = Assert.Single(logEvents, logEvent => logEvent.Fields.GetValueOrDefault("TraceId") == traceId
            && logEvent.Fields.GetValueOrDefault("Outcome") == "Recorded");
        Assert.Equal(LogLevel.Information, recordedEvent.Level);
        Assert.Equal("/cashmovements.v1.CashMovements/SubmitMovement", recordedEvent.Fields["Operation"]);
        Assert.Null(recordedEvent.Exception);
        foreach (LogEvent logEvent in logEvents)
        {
            string content = logEvent.Message + string.Join(" ", logEvent.Fields.Values);
            Assert.DoesNotContain(movement.Narration, content);
            Assert.DoesNotContain(movement.Amount, content);
        }
    }

    [Fact]
    public async Task ClientCancellationReachesTheDatabaseReadAndTheNextCallSucceeds()
    {
        string accountId = await fixture.CreateAccountAsync();
        GetBalanceRequest request = new GetBalanceRequest { AccountId = accountId };
        TaskCompletionSource readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource readStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan timeout = TimeSpan.FromSeconds(15);
        await fixture.WithReaderProbeAsync(async (_, cancellationToken) =>
        {
            readStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                readStopped.TrySetResult();
            }
        }, async () =>
        {
            using CancellationTokenSource cancellationSource = new CancellationTokenSource(timeout);
            using AsyncUnaryCall<GetBalanceReply> call = fixture.Client.GetBalanceAsync(request, cancellationToken: cancellationSource.Token);
            await readStarted.Task.WaitAsync(timeout);
            cancellationSource.Cancel();
            RpcException exception = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);
            Assert.Equal(StatusCode.Cancelled, exception.StatusCode);
            await readStopped.Task.WaitAsync(timeout);
            LogEvent cancellationEvent = await fixture.Logs.WaitForOutcomeAsync("Cancelled");
            Assert.Equal(LogLevel.Debug, cancellationEvent.Level);
            Assert.False(string.IsNullOrWhiteSpace(cancellationEvent.Fields["TraceId"]));
            Assert.Null(cancellationEvent.Exception);
        });
        Assert.Equal("0.00", (await fixture.Client.GetBalanceAsync(request)).Balance);
    }
}
