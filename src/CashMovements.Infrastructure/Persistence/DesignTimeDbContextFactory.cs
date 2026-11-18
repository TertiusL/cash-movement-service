using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CashMovements.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<CashMovementsDbContext>
{
    public CashMovementsDbContext CreateDbContext(string[] args)
    {
        // Offline model generation needs a provider. Supply container credentials through
        // ConnectionStrings__CashMovements before applying migrations to a database.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__CashMovements")
            ?? "Server=tcp:127.0.0.1,14333;Database=CashMovements;Encrypt=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<CashMovementsDbContext>()
            .UseSqlServer(connectionString)
            .Options;
        return new CashMovementsDbContext(options);
    }
}
