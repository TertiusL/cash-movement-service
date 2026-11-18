using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashMovements.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "dbo");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "dbo",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.AccountId);
                    table.UniqueConstraint("AK_Accounts_AccountId_Currency", x => new { x.AccountId, x.Currency });
                    table.CheckConstraint("CK_Accounts_AccountId", "DATALENGTH(REPLACE(TRANSLATE([AccountId], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_', REPLICATE(N'X', 39)), N'X', N'')) = 0\nAND [AccountId] LIKE N'%[A-Z0-9]%'");
                    table.CheckConstraint("CK_Accounts_Currency", "DATALENGTH([Currency]) = 6 AND [Currency] IN (N'ZAR', N'JPY', N'BHD')");
                });

            migrationBuilder.CreateTable(
                name: "Movements",
                schema: "dbo",
                columns: table => new
                {
                    MovementId = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    AccountId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false, collation: "Latin1_General_100_BIN2"),
                    ExternalRef = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Currency = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false, collation: "Latin1_General_100_BIN2"),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2(7)", nullable: false),
                    Narration = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Movements", x => x.MovementId);
                    table.CheckConstraint("CK_Movements_AccountId", "DATALENGTH(REPLACE(TRANSLATE([AccountId], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_', REPLICATE(N'X', 39)), N'X', N'')) = 0\nAND [AccountId] LIKE N'%[A-Z0-9]%'");
                    table.CheckConstraint("CK_Movements_Amount_MinorUnit", "([Currency] = N'ZAR' AND [Amount] % 0.01 = 0) OR\n([Currency] = N'JPY' AND [Amount] % 1 = 0) OR\n([Currency] = N'BHD' AND [Amount] % 0.001 = 0)");
                    table.CheckConstraint("CK_Movements_Amount_Nonzero", "[Amount] <> 0");
                    table.CheckConstraint("CK_Movements_ExternalRef", "DATALENGTH(REPLACE(TRANSLATE([ExternalRef], N'ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-/_', REPLICATE(N'X', 39)), N'X', N'')) = 0\nAND [ExternalRef] LIKE N'%[A-Z0-9]%'");
                    table.ForeignKey(
                        name: "FK_Movements_Accounts_AccountId_Currency",
                        columns: x => new { x.AccountId, x.Currency },
                        principalSchema: "dbo",
                        principalTable: "Accounts",
                        principalColumns: new[] { "AccountId", "Currency" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_AccountId_Currency",
                schema: "dbo",
                table: "Movements",
                columns: new[] { "AccountId", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_Movements_AccountId_OccurredAtUtc_MovementId",
                schema: "dbo",
                table: "Movements",
                columns: new[] { "AccountId", "OccurredAtUtc", "MovementId" });

            migrationBuilder.CreateIndex(
                name: "UX_Movements_AccountId_ExternalRef",
                schema: "dbo",
                table: "Movements",
                columns: new[] { "AccountId", "ExternalRef" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Movements",
                schema: "dbo");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "dbo");
        }
    }
}
