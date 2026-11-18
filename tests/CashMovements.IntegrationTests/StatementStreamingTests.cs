namespace CashMovements.IntegrationTests;

public sealed class StatementStreamingTests : IClassFixture<SqlServerFixture>
{
    private static readonly DateTime JulyStart = new(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private const int JulyMovementCount = 50_001;
    private readonly SqlServerFixture fixture;
    private readonly ITestOutputHelper output;

    public StatementStreamingTests(SqlServerFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    public async Task GeneratedClientConsumesSmallAndLargeStatementsIncrementally()
    {
        await fixture.LoadLargeDemoAsync();
        foreach (int expectedCount in new[] { 1_000, JulyMovementCount })
        {
            int rowQueries = 0;
            SqlCommand? capturedQuery = null;
            await fixture.WithReaderProbeAsync((command, _) =>
            {
                if (IsStatementRowQuery(command))
                {
                    rowQueries++;
                    capturedQuery = ((SqlCommand)command).Clone();
                }
                return Task.CompletedTask;
            }, () => CheckStreamAsync(expectedCount));
            Assert.Equal(1, rowQueries);
            using SqlCommand rowQuery = Assert.IsType<SqlCommand>(capturedQuery);
            await ReportActualPlanAsync(rowQuery, expectedCount);
        }
    }

    private async Task CheckStreamAsync(int expectedCount)
    {
        ExportStatementRequest request = Request(expectedCount);
        long initialHeap = GC.GetTotalMemory(forceFullCollection: true);
        long midpointHeap = 0;
        using AsyncServerStreamingCall<StatementFrame> call = fixture.Client.ExportStatement(
            request, deadline: DateTime.UtcNow.AddMinutes(2));
        IAsyncStreamReader<StatementFrame> frames = call.ResponseStream;
        Assert.True(await frames.MoveNext(CancellationToken.None));
        Assert.Equal(new StatementHeader
        {
            AccountId = "ACC-DEMO", Currency = "ZAR", FromInclusive = request.FromInclusive,
            ToExclusive = request.ToExclusive, OpeningBalance = "100.00"
        }, frames.Current.Header);

        int count = 0;
        decimal runningBalance = 100.00m;
        while (await frames.MoveNext(CancellationToken.None))
        {
            count++;
            StatementRow row = Assert.IsType<StatementRow>(frames.Current.Row);
            decimal expectedAmount = count == JulyMovementCount ? 0.50m : (count % 2 == 1 ? -1m : 1m);
            runningBalance += expectedAmount;
            Assert.Equal($"DEMO-{count}", row.Movement.ExternalRef);
            Assert.Equal(JulyStart.AddSeconds(count - 1), row.Movement.OccurredAt.ToDateTime());
            Assert.Equal(expectedAmount.ToString("F2", CultureInfo.InvariantCulture), row.Movement.Amount);
            Assert.Equal(runningBalance.ToString("F2", CultureInfo.InvariantCulture), row.RunningBalance);
            if (count == expectedCount / 2)
                midpointHeap = GC.GetTotalMemory(forceFullCollection: true);
        }

        Assert.Equal(StatusCode.OK, call.GetStatus().StatusCode);
        Assert.Equal(expectedCount, count);
        Assert.Equal(expectedCount == JulyMovementCount ? 100.50m : 100.00m, runningBalance);
        long completedHeap = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine($"Statement diagnostic: {count} rows, process-wide live managed bytes (server and client): " +
            $"before={initialHeap}, midpoint={midpointHeap}, completed={completedHeap}.");
    }

    [Fact]
    public async Task CancellingAfterMovementRowsReleasesTheActiveReaderAndSnapshot()
    {
        await fixture.LoadLargeDemoAsync();
        TimeSpan timeout = TimeSpan.FromSeconds(30);
        TaskCompletionSource<SqlTransaction> activeTransaction = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<(DbDataReader Reader, int ReadCount)> disposingReader = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await fixture.WithReaderProbeAsync((command, _) =>
        {
            if (IsStatementRowQuery(command))
                activeTransaction.TrySetResult((SqlTransaction)command.Transaction!);
            return Task.CompletedTask;
        }, async () =>
        {
            using CancellationTokenSource cancellation = new(timeout);
            using AsyncServerStreamingCall<StatementFrame> call = fixture.Client.ExportStatement(
                Request(JulyMovementCount), cancellationToken: cancellation.Token);
            IAsyncStreamReader<StatementFrame> frames = call.ResponseStream;
            Assert.True(await frames.MoveNext(CancellationToken.None));
            Assert.NotNull(frames.Current.Header);
            for (int count = 1; count <= 8; count++)
            {
                Assert.True(await frames.MoveNext(CancellationToken.None));
                Assert.Equal($"DEMO-{count}", frames.Current.Row.Movement.ExternalRef);
            }

            // Stop draining the HTTP/2 stream. Its receive window bounds how far the server can advance.
            SqlTransaction transaction = await activeTransaction.Task.WaitAsync(timeout);
            SqlConnection connection = Assert.IsType<SqlConnection>(transaction.Connection);
            Assert.Equal(IsolationLevel.Snapshot, transaction.IsolationLevel);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.False(disposingReader.Task.IsCompleted);
            cancellation.Cancel();
            RpcException failure = await Assert.ThrowsAsync<RpcException>(async () =>
            {
                while (await frames.MoveNext(CancellationToken.None))
                {
                    // Already transmitted frames may arrive before the terminal cancellation.
                }
            });
            Assert.Equal(StatusCode.Cancelled, failure.StatusCode);
            (DbDataReader reader, int readCount) = await disposingReader.Task.WaitAsync(timeout);
            // The terminal operation log is emitted after the async iterator's cleanup finishes.
            LogEvent terminalEvent = await fixture.Logs.WaitForOutcomeAsync("Cancelled");
            Assert.Equal(LogLevel.Debug, terminalEvent.Level);
            Assert.InRange(readCount, 8, JulyMovementCount - 1);
            Assert.True(reader.IsClosed);
            Assert.Null(transaction.Connection);
            Assert.Equal(ConnectionState.Closed, connection.State);
            output.WriteLine($"Statement diagnostic: cancelled after 8 client rows, SQL reader disposed after {readCount} reads.");
        }, (command, eventData) =>
        {
            if (IsStatementRowQuery(command))
                disposingReader.TrySetResult((eventData.DataReader, eventData.ReadCount));
        });
        GetBalanceReply balance = await fixture.Client.GetBalanceAsync(new GetBalanceRequest { AccountId = "ACC-DEMO" });
        Assert.Equal("100.50", balance.Balance);
    }

    private static bool IsStatementRowQuery(DbCommand command)
    {
        return command.CommandText.Contains("Statement movements", StringComparison.Ordinal);
    }

    private static ExportStatementRequest Request(int count)
    {
        DateTime toExclusive = count == JulyMovementCount ? JulyStart.AddMonths(1) : JulyStart.AddSeconds(count);
        return new ExportStatementRequest
        {
            AccountId = "acc-demo", FromInclusive = Timestamp.FromDateTime(JulyStart),
            ToExclusive = Timestamp.FromDateTime(toExclusive)
        };
    }

    private async Task ReportActualPlanAsync(SqlCommand rowQuery, int expectedCount)
    {
        await using SqlConnection connection = fixture.CreateConnection();
        await connection.OpenAsync();
        using SqlCommand planMode = new("SET STATISTICS XML ON", connection);
        await planMode.ExecuteNonQueryAsync();
        try
        {
            // Replay the emitted parameterized query separately from the measured gRPC call.
            // SQL returns the movement rows first, followed by its actual execution plan.
            rowQuery.Transaction = null;
            rowQuery.Connection = connection;
            await using SqlDataReader reader = await rowQuery.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            int rowsRead = 0;
            while (await reader.ReadAsync())
                rowsRead++;
            Assert.Equal(expectedCount, rowsRead);
            Assert.True(await reader.NextResultAsync());
            Assert.True(await reader.ReadAsync());
            XDocument plan = XDocument.Parse(reader.GetString(0));
            XNamespace planNamespace = plan.Root!.Name.Namespace;
            string operators = string.Join(", ", plan.Descendants(planNamespace + "RelOp")
                .Select(element => (string?)element.Attribute("PhysicalOp")));
            string indexes = string.Join(", ", plan.Descendants(planNamespace + "Object")
                .Select(element => (string?)element.Attribute("Index")).OfType<string>().Distinct());
            Assert.NotEmpty(operators);
            output.WriteLine($"Statement diagnostic: {rowsRead} rows, actual operators={operators}, indexes={indexes}.");
            output.WriteLine($"Statement diagnostic SQL: {rowQuery.CommandText}");
        }
        finally
        {
            planMode.CommandText = "SET STATISTICS XML OFF";
            await planMode.ExecuteNonQueryAsync();
        }
    }
}
