public static class Program
{
    public static void Main(string[] args)
    {
        BuildApp(WebApplication.CreateBuilder(args)).Run();
    }

    public static WebApplication BuildApp(WebApplicationBuilder builder)
    {
        string connectionString = builder.Configuration.GetConnectionString("CashMovements")
            ?? throw new InvalidOperationException("Set ConnectionStrings__CashMovements before starting the host.");

        // EF logs failed inserts before the store can classify a concurrent retry.
        // The RPC boundary owns runtime outcome and exception logs.
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
        builder.Logging.AddFilter("Grpc.AspNetCore.Server.ServerCallHandler", LogLevel.Warning);
        builder.Services.AddDbContextFactory<CashMovementsDbContext>((serviceProvider, options) =>
            options.UseSqlServer(connectionString).AddInterceptors(serviceProvider.GetServices<IInterceptor>()));
        builder.Services.AddScoped<IMovementStore, SqlMovementStore>();
        builder.Services.AddScoped<LedgerService>();
        builder.Services.AddSingleton<IsTemporaryStoreFailure>(SqlFailure.IsTemporary);
        builder.Services.AddGrpc(options => options.Interceptors.Add<UnaryRpcInterceptor>());

        WebApplication application = builder.Build();
        application.MapGrpcService<CashMovementsEndpoint>();
        return application;
    }
}
