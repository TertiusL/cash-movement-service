namespace CashMovements.IntegrationTests;

public sealed class SqlServerFixture : IAsyncLifetime
{
    private readonly string databaseName = $"CashMovements_Test_{Guid.NewGuid():N}";
    private readonly CommandProbe probe = new();
    private string adminConnectionString = null!;
    private string testConnectionString = null!;
    private bool ownsDatabase;
    private WebApplication? host;
    private GrpcChannel? channel;
    internal LedgerClient Client { get; private set; } = null!;
    internal string Endpoint { get; private set; } = null!;
    internal TestLogProvider Logs { get; } = new();
    internal StatementCommitFault CommitFault { get; } = new();

    public async Task InitializeAsync()
    {
        string suppliedConnectionString = Environment.GetEnvironmentVariable("CASHMOVEMENTS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Set CASHMOVEMENTS_TEST_CONNECTION to your Docker SQL Server test instance.");
        SqlConnectionStringBuilder connectionStringBuilder = new(suppliedConnectionString) { InitialCatalog = "master" };
        adminConnectionString = connectionStringBuilder.ConnectionString;
        connectionStringBuilder.InitialCatalog = databaseName;
        testConnectionString = connectionStringBuilder.ConnectionString;

        await ExecuteAdminAsync($"CREATE DATABASE [{databaseName}]");
        ownsDatabase = true;
        try
        {
            await ExecuteAdminAsync($"ALTER DATABASE [{databaseName}] SET ALLOW_SNAPSHOT_ISOLATION ON");
            DbContextOptions<CashMovementsDbContext> databaseOptions = new DbContextOptionsBuilder<CashMovementsDbContext>().UseSqlServer(testConnectionString).Options;
            await using (CashMovementsDbContext databaseContext = new(databaseOptions))
            {
                await databaseContext.Database.MigrateAsync();
            }

            WebApplicationBuilder hostBuilder = WebApplication.CreateBuilder();
            hostBuilder.Configuration["ConnectionStrings:CashMovements"] = testConnectionString;
            hostBuilder.Logging.AddProvider(Logs);
            hostBuilder.Logging.AddFilter<TestLogProvider>(typeof(RpcOutcomeInterceptor).FullName, LogLevel.Debug);
            hostBuilder.Services.AddSingleton<IInterceptor>(probe);
            hostBuilder.Services.AddSingleton<IInterceptor>(CommitFault);
            hostBuilder.WebHost.ConfigureKestrel(server =>
                server.Listen(IPAddress.Loopback, 0, listener => listener.Protocols = HttpProtocols.Http2));
            host = Program.BuildApp(hostBuilder);
            await host.StartAsync();
            Endpoint = host.Urls.Single();
            channel = GrpcChannel.ForAddress(Endpoint);
            Client = new LedgerClient(channel);
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        channel?.Dispose();
        if (host is not null)
        {
            await host.DisposeAsync();
            host = null;
        }
        if (ownsDatabase)
        {
            await ExecuteAdminAsync($"ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]");
            ownsDatabase = false;
        }
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using SqlConnection connection = new SqlConnection(adminConnectionString);
        await connection.OpenAsync();
        using SqlCommand command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    internal SqlConnection CreateConnection()
    {
        return new SqlConnection(testConnectionString);
    }

    internal async Task LoadLargeDemoAsync()
    {
        string script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "db", "large-demo.sql"));
        await using SqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        foreach (string batch in Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch))
                continue;
            using SqlCommand command = new(batch, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    internal Task WithSyntheticOpeningAsync(string accountId, string openingAmount, Func<Task> runTest)
    {
        return WithReaderProbeAsync((command, _) =>
        {
            // Replace only the input to the opening SUM. SQL still applies its production range/scale checks.
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
        }, runTest);
    }

    internal async Task<string> CreateAccountAsync(string currency = "ZAR")
    {
        string accountId = $"T-{Guid.NewGuid():N}".ToUpperInvariant();
        await using SqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        using SqlCommand command = new SqlCommand("INSERT INTO dbo.Accounts (AccountId, Currency) VALUES (@id, @currency)", connection);
        command.Parameters.AddWithValue("@id", accountId);
        command.Parameters.AddWithValue("@currency", currency);
        await command.ExecuteNonQueryAsync();
        return accountId;
    }

    internal async Task<List<StoredMovement>> ReadRowsAsync(string accountId)
    {
        await using SqlConnection connection = CreateConnection();
        await connection.OpenAsync();
        using SqlCommand command = new SqlCommand("""
            SELECT MovementId, AccountId, ExternalRef, Currency, Amount, OccurredAtUtc, Narration
            FROM dbo.Movements WHERE AccountId = @id ORDER BY MovementId
            """, connection);
        command.Parameters.AddWithValue("@id", accountId);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        List<StoredMovement> movements = new List<StoredMovement>();
        while (await reader.ReadAsync())
            movements.Add(new StoredMovement(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetDecimal(4), DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc),
                reader.GetString(6)));
        return movements;
    }

    // Tests in this fixture's class run serially. Each probe is scoped to one test action.
    internal async Task WithReaderProbeAsync(Func<DbCommand, CancellationToken, Task> beforeReader, Func<Task> runTest,
        Action<DbCommand, DataReaderDisposingEventData>? onReaderDisposing = null)
    {
        probe.BeforeReader = beforeReader;
        probe.OnReaderDisposing = onReaderDisposing;
        try
        {
            await runTest();
        }
        finally
        {
            probe.BeforeReader = null;
            probe.OnReaderDisposing = null;
        }
    }

    private sealed class CommandProbe : DbCommandInterceptor
    {
        internal Func<DbCommand, CancellationToken, Task>? BeforeReader { get; set; }
        internal Action<DbCommand, DataReaderDisposingEventData>? OnReaderDisposing { get; set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (BeforeReader is Func<DbCommand, CancellationToken, Task> beforeReader)
                await beforeReader(command, cancellationToken);
            return result;
        }

        public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            OnReaderDisposing?.Invoke(command, eventData);
            return result;
        }
    }
}
