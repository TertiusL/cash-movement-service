using Microsoft.EntityFrameworkCore;

namespace CashMovements.Infrastructure.Persistence;

public sealed class CashMovementsDbContext(DbContextOptions<CashMovementsDbContext> options) : DbContext(options)
{
    private const string IdentifierCollation = "Latin1_General_100_BIN2";

    internal DbSet<AccountRecord> Accounts => Set<AccountRecord>();
    internal DbSet<MovementRecord> Movements => Set<MovementRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");

        var accounts = modelBuilder.Entity<AccountRecord>();
        accounts.ToTable("Accounts", table =>
        {
            table.HasCheckConstraint("CK_Accounts_AccountId", CanonicalIdentifier("[AccountId]"));
            table.HasCheckConstraint("CK_Accounts_Currency",
                "DATALENGTH([Currency]) = 6 AND [Currency] IN (N'ZAR', N'JPY', N'BHD')");
        });
        accounts.HasKey(account => account.AccountId);
        accounts.HasAlternateKey(account => new { account.AccountId, account.Currency });
        accounts.Property(account => account.AccountId).HasMaxLength(64).UseCollation(IdentifierCollation);
        accounts.Property(account => account.Currency).HasMaxLength(3).UseCollation(IdentifierCollation);

        var movements = modelBuilder.Entity<MovementRecord>();
        movements.ToTable("Movements", table =>
        {
            table.HasCheckConstraint("CK_Movements_AccountId", CanonicalIdentifier("[AccountId]"));
            table.HasCheckConstraint("CK_Movements_ExternalRef", CanonicalIdentifier("[ExternalRef]"));
            table.HasCheckConstraint("CK_Movements_Amount_Nonzero", "[Amount] <> 0");
            table.HasCheckConstraint("CK_Movements_Amount_MinorUnit", """
                ([Currency] = N'ZAR' AND [Amount] % 0.01 = 0) OR
                ([Currency] = N'JPY' AND [Amount] % 1 = 0) OR
                ([Currency] = N'BHD' AND [Amount] % 0.001 = 0)
                """);
        });
        movements.HasKey(movement => movement.MovementId);
        movements.Property(movement => movement.MovementId).UseIdentityColumn();
        movements.Property(movement => movement.AccountId).HasMaxLength(64).UseCollation(IdentifierCollation);
        movements.Property(movement => movement.ExternalRef).HasMaxLength(128).UseCollation(IdentifierCollation);
        movements.Property(movement => movement.Currency).HasMaxLength(3).UseCollation(IdentifierCollation);
        movements.Property(movement => movement.Amount).HasPrecision(19, 4);
        movements.Property(movement => movement.OccurredAtUtc)
            .HasColumnType("datetime2(7)")
            .HasConversion(value => RequireUtc(value), value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        movements.Property(movement => movement.Narration).HasMaxLength(256);

        movements.HasOne<AccountRecord>()
            .WithMany()
            .HasForeignKey(movement => new { movement.AccountId, movement.Currency })
            .HasPrincipalKey(account => new { account.AccountId, account.Currency })
            .OnDelete(DeleteBehavior.Restrict);
        movements.HasIndex(movement => new { movement.AccountId, movement.ExternalRef })
            .IsUnique()
            .HasDatabaseName("UX_Movements_AccountId_ExternalRef");
        movements.HasIndex(movement => new { movement.AccountId, movement.OccurredAtUtc, movement.MovementId })
            .HasDatabaseName("IX_Movements_AccountId_OccurredAtUtc_MovementId");
    }

    private static string CanonicalIdentifier(string column)
    {
        const string allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_";
        // Fold every permitted character to X and remove it. DATALENGTH also detects trailing spaces.
        return $"""
            DATALENGTH(REPLACE(TRANSLATE({column}, N'{allowed}', REPLICATE(N'X', {allowed.Length})), N'X', N'')) = 0
            AND {column} LIKE N'%[A-Z0-9]%'
            """;
    }

    private static DateTime RequireUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc
            ? value
            : throw new ArgumentException("A movement timestamp must be UTC.", nameof(value));
}
