using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CashMovements.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CoverStatementIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Movements_AccountId_OccurredAtUtc_MovementId",
                schema: "dbo",
                table: "Movements");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_AccountId_OccurredAtUtc_MovementId",
                schema: "dbo",
                table: "Movements",
                columns: new[] { "AccountId", "OccurredAtUtc", "MovementId" })
                .Annotation("SqlServer:Include", new[] { "ExternalRef", "Currency", "Amount", "Narration" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Movements_AccountId_OccurredAtUtc_MovementId",
                schema: "dbo",
                table: "Movements");

            migrationBuilder.CreateIndex(
                name: "IX_Movements_AccountId_OccurredAtUtc_MovementId",
                schema: "dbo",
                table: "Movements",
                columns: new[] { "AccountId", "OccurredAtUtc", "MovementId" });
        }
    }
}
