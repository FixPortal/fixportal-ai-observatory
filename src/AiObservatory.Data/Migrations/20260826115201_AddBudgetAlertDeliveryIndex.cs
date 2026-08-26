using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetAlertDeliveryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder
                .CreateIndex(
                    name: "IX_BudgetAlertClaims_Deliverable",
                    table: "BudgetAlertClaims",
                    columns: new[] { "CreatedAt", "Id" },
                    filter: "\"EmailSentAt\" IS NULL"
                )
                .Annotation("Npgsql:IndexInclude", new[] { "EmailLeaseAcquiredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_BudgetAlertClaims_Deliverable", table: "BudgetAlertClaims");
        }
    }
}
