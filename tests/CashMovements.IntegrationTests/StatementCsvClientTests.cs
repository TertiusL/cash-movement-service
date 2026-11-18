namespace CashMovements.IntegrationTests;

public sealed class StatementCsvClientTests : IClassFixture<SqlServerFixture>, IDisposable
{
    private static readonly string[] Columns =
        ["recordType", "accountId", "externalRef", "currency", "amount", "occurredAt", "narration", "runningBalance"];
    private const string From = "2024-07-01T00:00:00Z";
    private const string To = "2024-08-01T00:00:00Z";
    private static readonly DateTime JulyStart = new(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly SqlServerFixture fixture;
    private readonly DirectoryInfo exportDirectory = Directory.CreateTempSubdirectory("cashmovements-csv-");
    private readonly string destination;

    public StatementCsvClientTests(SqlServerFixture fixture)
    {
        this.fixture = fixture;
        destination = Path.Combine(exportDirectory.FullName, "statement.csv");
    }

    public void Dispose()
    {
        exportDirectory.Delete(recursive: true);
    }

    [Fact]
    public async Task CsvRoundTripsNarrationUtcTickAndTotalsWiderThanOnePosting()
    {
        string accountId = await fixture.CreateAccountAsync("BHD");
        const string narration = "  Café, \"دفعة\"\r\n東京  ";
        DateTime firstInstant = JulyStart.AddTicks(1);
        await RecordAsync(accountId, "ONE", "-999999999999999.999", firstInstant, narration);
        await RecordAsync(accountId, "TWO", "-999999999999999.999", firstInstant.AddTicks(1), "fee");

        using StringWriter progress = new();
        int exitCode = await ExportAsync(accountId.ToLowerInvariant(), progress, from: "2024-07-01T02:00:00.0000001+02:00");
        Assert.Equal(0, exitCode);
        Assert.Contains("Completed 2", progress.ToString());
        Assert.Empty(Partials());

        using TextFieldParser parser = OpenCsv(destination);
        Assert.Equal(Columns, parser.ReadFields());
        AssertSummary(parser, "opening", accountId, "BHD", "0.000");
        Assert.Equal(new[] { "movement", accountId, "ONE", "BHD", "-999999999999999.999",
            "2024-07-01T00:00:00.0000001Z", narration, "-999999999999999.999" }, parser.ReadFields());
        Assert.Equal(new[] { "movement", accountId, "TWO", "BHD", "-999999999999999.999",
            "2024-07-01T00:00:00.0000002Z", "fee", "-1999999999999999.998" }, parser.ReadFields());
        AssertSummary(parser, "closing", accountId, "BHD", "-1999999999999999.998");
        Assert.Null(parser.ReadFields());

        string contents = await File.ReadAllTextAsync(destination, new UTF8Encoding(false, true));
        Assert.Contains(",\"  Café, \"\"دفعة\"\"\r\n東京  \",", contents);
        Assert.DoesNotContain('\n', contents.Replace("\r\n", "", StringComparison.Ordinal));
        Assert.EndsWith("\r\n", contents);
    }

    [Fact]
    public async Task EmptyPeriodWritesMatchingNonzeroSummaries()
    {
        string accountId = await fixture.CreateAccountAsync("BHD");
        await RecordAsync(accountId, "EARLIER", "47.125", JulyStart.AddTicks(-1), "deposit");
        using StringWriter progress = new();
        Assert.Equal(0, await ExportAsync(accountId, progress));
        Assert.Contains("Completed 0", progress.ToString());
        using TextFieldParser parser = OpenCsv(destination);
        Assert.Equal(Columns, parser.ReadFields());
        AssertSummary(parser, "opening", accountId, "BHD", "47.125");
        AssertSummary(parser, "closing", accountId, "BHD", "47.125");
        Assert.Null(parser.ReadFields());
    }

    [Fact]
    public async Task LargeExportShowsProgressWhilePartialGrowsAndPublishesAll50001Rows()
    {
        await fixture.LoadLargeDemoAsync();
        long firstVisibleSize = 0;
        long largestVisibleSize = 0;
        using ExportProgressWriter progress = new(line =>
        {
            if (line.StartsWith("Processed ", StringComparison.Ordinal))
            {
                Assert.False(File.Exists(destination));
                long length = new FileInfo(Assert.Single(Partials())).Length;
                if (firstVisibleSize == 0)
                    firstVisibleSize = length;
                largestVisibleSize = Math.Max(largestVisibleSize, length);
            }
        });
        Assert.Equal(0, await ExportAsync("acc-demo", progress));
        Assert.True(firstVisibleSize > 0 && largestVisibleSize > firstVisibleSize);
        Assert.Contains("Completed 50001", progress.ToString());
        Assert.Contains(destination, progress.ToString());
        Assert.Empty(Partials());

        using TextFieldParser parser = OpenCsv(destination);
        Assert.Equal(Columns, parser.ReadFields());
        AssertSummary(parser, "opening", "ACC-DEMO", "ZAR", "100.00");
        decimal balance = 100.00m;
        for (int index = 1; index <= 50_001; index++)
        {
            decimal amount = index == 50_001 ? 0.50m : index % 2 == 1 ? -1.00m : 1.00m;
            balance += amount;
            string narration = index == 50_001 ? "Final credit" : index % 2 == 1 ? "Demo withdrawal" : "Demo deposit";
            Assert.Equal(new[] { "movement", "ACC-DEMO", $"DEMO-{index}", "ZAR", amount.ToString("F2", CultureInfo.InvariantCulture),
                JulyStart.AddSeconds(index - 1).ToString("O", CultureInfo.InvariantCulture), narration,
                balance.ToString("F2", CultureInfo.InvariantCulture) }, parser.ReadFields());
        }
        AssertSummary(parser, "closing", "ACC-DEMO", "ZAR", "100.50");
        Assert.Null(parser.ReadFields());
    }

    [Fact]
    public async Task CancellationDuringExportLeavesOnlyAnIdentifiedPartialAndARetryStartsFresh()
    {
        await fixture.LoadLargeDemoAsync();
        using CancellationTokenSource cancellation = new();
        using ExportProgressWriter progress = new(line =>
        {
            if (line.StartsWith("Processed ", StringComparison.Ordinal) && new FileInfo(Assert.Single(Partials())).Length > 0)
            {
                Assert.False(File.Exists(destination));
                cancellation.Cancel();
            }
        });
        Assert.Equal(130, await ExportAsync("ACC-DEMO", progress, cancellation.Token));
        string partial = AssertIncomplete(progress);
        Assert.DoesNotContain(File.ReadLines(partial), line => line.StartsWith("closing,", StringComparison.Ordinal));
        int movementCount = File.ReadLines(partial).Count(line => line.StartsWith("movement,", StringComparison.Ordinal));
        Assert.InRange(movementCount, 1, 50_000);

        using StringWriter retryProgress = new();
        Assert.Equal(0, await ExportAsync("ACC-DEMO", retryProgress, from: To, to: "2024-09-01T00:00:00Z"));
        Assert.Equal(partial, Assert.Single(Partials()));
        using TextFieldParser parser = OpenCsv(destination);
        Assert.Equal(Columns, parser.ReadFields());
        AssertSummary(parser, "opening", "ACC-DEMO", "ZAR", "100.50");
        AssertSummary(parser, "closing", "ACC-DEMO", "ZAR", "100.50");
        Assert.Null(parser.ReadFields());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonOkAfterLastValidRowLeavesReadablePartialWithoutClosing(bool failAtSnapshotCommit)
    {
        string accountId = await fixture.CreateAccountAsync("BHD");
        await RecordAsync(accountId, "VALID", "0.001", JulyStart, "first");
        using StringWriter progress = new();
        string opening;
        string running;
        if (failAtSnapshotCommit)
        {
            opening = "0.000";
            running = "0.001";
            fixture.CommitFault.Enabled = true;
            try
            {
                Assert.Equal(1, await ExportAsync(accountId, progress));
            }
            finally
            {
                fixture.CommitFault.Enabled = false;
            }
            Assert.Contains("Internal", progress.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            opening = "999999999999999999999999.998";
            running = "999999999999999999999999.999";
            await RecordAsync(accountId, "OVER-LIMIT", "0.001", JulyStart.AddTicks(1), "overflow");
            await fixture.WithSyntheticOpeningAsync(accountId, opening, async () =>
            {
                Assert.Equal(1, await ExportAsync(accountId, progress));
            });
            Assert.Contains("OutOfRange", progress.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        string partial = AssertIncomplete(progress);
        using TextFieldParser parser = OpenCsv(partial);
        Assert.Equal(Columns, parser.ReadFields());
        AssertSummary(parser, "opening", accountId, "BHD", opening);
        Assert.Equal(new[] { "movement", accountId, "VALID", "BHD", "0.001",
            "2024-07-01T00:00:00.0000000Z", "first", running }, parser.ReadFields());
        Assert.Null(parser.ReadFields());
    }

    [Fact]
    public async Task DestinationCreatedDuringExportSurvivesPublicationFailure()
    {
        await fixture.LoadLargeDemoAsync();
        const string competingContents = "Another export owns this filename.";
        using ExportProgressWriter progress = new(line =>
        {
            if (line.StartsWith("Processed ", StringComparison.Ordinal) && !File.Exists(destination))
                File.WriteAllText(destination, competingContents);
        });
        Assert.Equal(1, await ExportAsync("ACC-DEMO", progress, to: "2024-07-01T00:33:20Z"));
        Assert.Equal(competingContents, await File.ReadAllTextAsync(destination));
        string partial = Assert.Single(Partials());
        Assert.Contains(partial, progress.ToString());
        Assert.DoesNotContain("Completed ", progress.ToString());
        Assert.Equal("closing,ACC-DEMO,,ZAR,,,,100.00", File.ReadLines(partial).Last());
    }

    private string AssertIncomplete(TextWriter progress)
    {
        Assert.False(File.Exists(destination));
        string partial = Assert.Single(Partials());
        Assert.Contains(partial, progress.ToString());
        Assert.DoesNotContain("Completed ", progress.ToString());
        return partial;
    }

    private IEnumerable<string> Partials()
    {
        return Directory.EnumerateFiles(exportDirectory.FullName, "statement.csv.*.partial");
    }

    private Task<int> ExportAsync(string accountId, TextWriter progress, CancellationToken cancellationToken = default,
        string from = From, string to = To)
    {
        return StatementClientProgram.RunAsync(
            ["--endpoint", fixture.Endpoint, "--account", accountId, "--from", from, "--to", to, "--output", destination],
            progress, cancellationToken);
    }

    private async Task RecordAsync(string accountId, string externalRef, string amount, DateTime occurredAt, string narration)
    {
        Movement movement = new() { AccountId = accountId, ExternalRef = externalRef, Currency = "BHD", Amount = amount,
            OccurredAt = Timestamp.FromDateTime(occurredAt), Narration = narration };
        SubmitMovementReply reply = await fixture.Client.SubmitMovementAsync(new SubmitMovementRequest { Movement = movement });
        Assert.Equal(SubmissionDisposition.Recorded, reply.Disposition);
    }

    private static TextFieldParser OpenCsv(string path)
    {
        TextFieldParser parser = new(path, new UTF8Encoding(false, true)) { TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        return parser;
    }

    private static void AssertSummary(TextFieldParser parser, string kind, string accountId, string currency, string balance)
    {
        Assert.Equal(new[] { kind, accountId, "", currency, "", "", "", balance }, parser.ReadFields());
    }
}
