namespace CashMovements.IntegrationTests;

public sealed class StatementRpcTests : IClassFixture<SqlServerFixture>
{
    private static readonly DateTime StartUtc = new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
    private static readonly DateTime EndUtc = StartUtc.AddDays(1);
    private readonly SqlServerFixture fixture;

    public StatementRpcTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    public async Task BoundariesOrderingAndEmptyPeriodsUseTheSameClosingRule()
    {
        string accountId = await fixture.CreateAccountAsync();
        // Insert the latest event first. Tied events also have references in reverse lexical order.
        Movement atEnd = await RecordAsync(accountId, "UPPER", "99.00", EndUtc);
        Movement firstAtStart = await RecordAsync(accountId, "Z-FIRST", "-30.00", StartUtc);
        Movement secondAtStart = await RecordAsync(accountId, "A-SECOND", "5.00", StartUtc);
        await RecordAsync(accountId, "OPENING", "100.00", StartUtc.AddTicks(-1));

        string otherAccount = await fixture.CreateAccountAsync();
        await RecordAsync(otherAccount, "OPENING", "800.00", StartUtc.AddTicks(-1));
        await RecordAsync(otherAccount, "Z-FIRST", "700.00", StartUtc);

        ExportStatementRequest period = Period(accountId.ToLowerInvariant(), StartUtc, EndUtc);
        using (AsyncServerStreamingCall<StatementFrame> call = Export(period))
        {
            await ExpectHeaderAsync(call, period, accountId, "ZAR", "100.00");
            await ExpectRowAsync(call, firstAtStart, "70.00");
            await ExpectRowAsync(call, secondAtStart, "75.00");
            await ExpectCompletionAsync(call);
        }

        ExportStatementRequest adjacentPeriod = Period(accountId, EndUtc, EndUtc.AddTicks(1));
        using (AsyncServerStreamingCall<StatementFrame> call = Export(adjacentPeriod))
        {
            await ExpectHeaderAsync(call, adjacentPeriod, accountId, "ZAR", "75.00");
            await ExpectRowAsync(call, atEnd, "174.00");
            await ExpectCompletionAsync(call);
        }

        ExportStatementRequest combinedPeriod = Period(accountId, StartUtc, EndUtc.AddTicks(1));
        using (AsyncServerStreamingCall<StatementFrame> call = Export(combinedPeriod))
        {
            await ExpectHeaderAsync(call, combinedPeriod, accountId, "ZAR", "100.00");
            await ExpectRowAsync(call, firstAtStart, "70.00");
            await ExpectRowAsync(call, secondAtStart, "75.00");
            await ExpectRowAsync(call, atEnd, "174.00");
            await ExpectCompletionAsync(call);
        }

        ExportStatementRequest emptyPeriod = Period(accountId, EndUtc.AddTicks(1), EndUtc.AddTicks(2));
        using (AsyncServerStreamingCall<StatementFrame> call = Export(emptyPeriod))
        {
            await ExpectHeaderAsync(call, emptyPeriod, accountId, "ZAR", "174.00");
            await ExpectCompletionAsync(call);
        }
    }

    [Fact]
    public async Task NeverUsedAccountReturnsOnlyAZeroOpeningInItsOwnCurrency()
    {
        string accountId = await fixture.CreateAccountAsync("JPY");
        ExportStatementRequest period = Period(accountId, StartUtc, EndUtc);
        using AsyncServerStreamingCall<StatementFrame> call = Export(period);
        await ExpectHeaderAsync(call, period, accountId, "JPY", "0");
        await ExpectCompletionAsync(call);
    }

    [Theory]
    [InlineData("missing start", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("missing end", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("sub-tick start", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("sub-tick end", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("invalid instant", StatusCode.InvalidArgument, BusinessErrorReason.InvalidTimestamp)]
    [InlineData("equal", StatusCode.InvalidArgument, BusinessErrorReason.InvalidRange)]
    [InlineData("reversed", StatusCode.InvalidArgument, BusinessErrorReason.InvalidRange)]
    [InlineData("bad account", StatusCode.InvalidArgument, BusinessErrorReason.InvalidIdentifier)]
    [InlineData("unknown account", StatusCode.NotFound, BusinessErrorReason.AccountNotFound)]
    public async Task InvalidRequestOrUnknownAccountFailsBeforeTheHeader(
        string scenario, StatusCode status, BusinessErrorReason reason)
    {
        string accountId = await fixture.CreateAccountAsync();
        ExportStatementRequest request = Period(accountId, StartUtc, EndUtc);
        switch (scenario)
        {
            case "missing start": request.FromInclusive = null; break;
            case "missing end": request.ToExclusive = null; break;
            case "sub-tick start": request.FromInclusive.Nanos += 1; break;
            case "sub-tick end": request.ToExclusive.Nanos += 1; break;
            case "invalid instant": request.ToExclusive.Seconds = 253402300800L; break;
            case "equal": request.ToExclusive = request.FromInclusive; break;
            case "reversed": request.ToExclusive = Timestamp.FromDateTime(StartUtc.AddTicks(-1)); break;
            case "bad account": request.AccountId += " "; break;
            case "unknown account": request.AccountId += "-MISSING"; break;
        }

        using AsyncServerStreamingCall<StatementFrame> call = Export(request);
        await MovementRpcTests.AssertRpcErrorAsync(() => call.ResponseStream.MoveNext(), status, reason);
    }

    [Theory]
    [InlineData(true, "97.00")]
    [InlineData(false, "127.00")]
    public async Task ActiveSnapshotExcludesBackdatedWritesThatTheNextExportSees(bool initiallyPopulated, string subsequentClosing)
    {
        string accountId = await fixture.CreateAccountAsync();
        await RecordAsync(accountId, "OPEN", "100.00", StartUtc.AddTicks(-1));
        Movement? original = initiallyPopulated ? await RecordAsync(accountId, "ORIGINAL", "-30.00", StartUtc) : null;
        ExportStatementRequest period = Period(accountId, StartUtc, EndUtc);
        DbTransaction? openingTransaction = null;
        Movement? backdatedRow = null;
        await fixture.WithReaderProbeAsync(async (command, _) =>
        {
            if (command.CommandText.Contains("SUM(m.Amount)", StringComparison.Ordinal))
            {
                openingTransaction = command.Transaction;
            }
            if (command.CommandText.Contains("Statement movements", StringComparison.Ordinal))
            {
                Assert.NotNull(openingTransaction);
                Assert.Equal(IsolationLevel.Snapshot, openingTransaction.IsolationLevel);
                Assert.Same(openingTransaction, command.Transaction);

                // Both writes commit on independent calls while the export is paused before its row query.
                await RecordAsync(accountId, "BACKDATED-OPEN", "20.00", StartUtc.AddTicks(-1));
                backdatedRow = await RecordAsync(accountId, "BACKDATED-ROW", "7.00", StartUtc.AddTicks(1));
            }
        }, async () =>
        {
            using AsyncServerStreamingCall<StatementFrame> call = Export(period);
            await ExpectHeaderAsync(call, period, accountId, "ZAR", "100.00");
            if (original is not null)
            {
                await ExpectRowAsync(call, original, "70.00");
            }
            await ExpectCompletionAsync(call);
        });

        Assert.NotNull(backdatedRow);
        using AsyncServerStreamingCall<StatementFrame> subsequent = Export(period);
        await ExpectHeaderAsync(subsequent, period, accountId, "ZAR", "120.00");
        if (original is not null)
        {
            await ExpectRowAsync(subsequent, original, "90.00");
        }
        await ExpectRowAsync(subsequent, backdatedRow, subsequentClosing);
        await ExpectCompletionAsync(subsequent);
    }

    [Theory]
    [InlineData("1000000000000000000000000.000", "0.001", null)]
    [InlineData("-1000000000000000000000000.000", "-0.001", null)]
    [InlineData("0.0001", "0.001", null)]
    [InlineData("999999999999999999999999.998", "0.001", "999999999999999999999999.999")]
    [InlineData("-999999999999999999999999.998", "-0.001", "-999999999999999999999999.999")]
    public async Task UnreportableTotalsFailBeforeEmittingTheAffectedFrame(
        string openingAmount, string movementAmount, string? lastReportableBalance)
    {
        string accountId = await fixture.CreateAccountAsync("BHD");
        Movement firstMovement = await RecordAsync(accountId, "AT-START", movementAmount, StartUtc, "BHD");
        if (lastReportableBalance is not null)
        {
            await RecordAsync(accountId, "AT-LIMIT", movementAmount, StartUtc.AddTicks(1), "BHD");
        }
        ExportStatementRequest period = Period(accountId, StartUtc, EndUtc);
        await fixture.WithReaderProbeAsync((command, _) =>
        {
            // Only replace the input to the SQL opening SUM. Keep its range/scale conversion and the real row query.
            const string movementSource = "dbo.Movements AS m";
            if (command.CommandText.Contains(movementSource, StringComparison.Ordinal))
            {
                const string syntheticSource = """
                    (VALUES (@testAccount, CAST(@testOpening AS decimal(38,4)), CAST('2000-01-01' AS datetime2(7))))
                    AS m(AccountId, Amount, OccurredAtUtc)
                    """;
                command.CommandText = command.CommandText.Replace(movementSource, syntheticSource, StringComparison.Ordinal);
                SqlParameterCollection parameters = ((SqlCommand)command).Parameters;
                parameters.Add("@testAccount", SqlDbType.NVarChar, 64).Value = accountId;
                parameters.Add("@testOpening", SqlDbType.VarChar, 40).Value = openingAmount;
            }
            return Task.CompletedTask;
        }, async () =>
        {
            using AsyncServerStreamingCall<StatementFrame> call = Export(period);
            if (lastReportableBalance is not null)
            {
                await ExpectHeaderAsync(call, period, accountId, "BHD", openingAmount);
                await ExpectRowAsync(call, firstMovement, lastReportableBalance);
            }
            await MovementRpcTests.AssertRpcErrorAsync(() => call.ResponseStream.MoveNext(),
                StatusCode.OutOfRange, BusinessErrorReason.AggregateOutOfRange);
        });
    }

    [Fact]
    public async Task FailureAfterHeaderEndsTheStreamWithASafeCorrelatedError()
    {
        string accountId = await fixture.CreateAccountAsync();
        ExportStatementRequest period = Period(accountId, StartUtc, EndUtc);
        const string diagnostic = "test-only confidential statement diagnostic";
        await fixture.WithReaderProbeAsync((command, _) =>
        {
            if (command.CommandText.Contains("Statement movements", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(diagnostic);
            }
            return Task.CompletedTask;
        }, async () =>
        {
            using AsyncServerStreamingCall<StatementFrame> call = Export(period);
            await ExpectHeaderAsync(call, period, accountId, "ZAR", "0.00");
            RpcException exception = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext());
            Assert.Equal(StatusCode.Internal, exception.StatusCode);
            Assert.DoesNotContain(diagnostic, exception.Status.Detail);
            string? traceId = exception.Trailers.Get("trace-id")?.Value;
            Assert.False(string.IsNullOrWhiteSpace(traceId));
            LogEvent failure = Assert.Single(fixture.Logs.Drain(), logEvent => logEvent.Exception?.Message == diagnostic);
            Assert.Equal(LogLevel.Error, failure.Level);
            Assert.Equal(traceId, failure.Fields["TraceId"]);
        });
    }

    private static ExportStatementRequest Period(string accountId, DateTime fromInclusive, DateTime toExclusive)
    {
        return new ExportStatementRequest
        {
            AccountId = accountId, FromInclusive = Timestamp.FromDateTime(fromInclusive), ToExclusive = Timestamp.FromDateTime(toExclusive)
        };
    }

    private AsyncServerStreamingCall<StatementFrame> Export(ExportStatementRequest request)
    {
        return fixture.Client.ExportStatement(request, deadline: DateTime.UtcNow.AddSeconds(30));
    }

    private async Task<Movement> RecordAsync(string accountId, string reference, string amount, DateTime occurredAt, string currency = "ZAR")
    {
        Movement movement = new Movement
        {
            AccountId = accountId, ExternalRef = reference, Currency = currency, Amount = amount,
            OccurredAt = Timestamp.FromDateTime(occurredAt), Narration = "  Café\r\n" + reference + "  "
        };
        using AsyncUnaryCall<SubmitMovementReply> call = fixture.Client.SubmitMovementAsync(
            new SubmitMovementRequest { Movement = movement }, deadline: DateTime.UtcNow.AddSeconds(15));
        Assert.Equal(SubmissionDisposition.Recorded, (await call.ResponseAsync).Disposition);
        return movement;
    }

    private static async Task ExpectHeaderAsync(AsyncServerStreamingCall<StatementFrame> call,
        ExportStatementRequest period, string accountId, string currency, string opening)
    {
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(new StatementFrame
        {
            Header = new StatementHeader
            {
                AccountId = accountId, Currency = currency, FromInclusive = period.FromInclusive,
                ToExclusive = period.ToExclusive, OpeningBalance = opening
            }
        }, call.ResponseStream.Current);
    }

    private static async Task ExpectRowAsync(AsyncServerStreamingCall<StatementFrame> call, Movement movement, string balance)
    {
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(new StatementFrame
        {
            Row = new StatementRow { Movement = movement, RunningBalance = balance }
        }, call.ResponseStream.Current);
    }

    private static async Task ExpectCompletionAsync(AsyncServerStreamingCall<StatementFrame> call)
    {
        Assert.False(await call.ResponseStream.MoveNext());
        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(call.GetTrailers().Get("trace-id")?.Value));
    }
}
