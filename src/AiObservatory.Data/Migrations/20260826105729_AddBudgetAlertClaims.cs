using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetAlertClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BudgetAlertClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BudgetRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodStart = table.Column<LocalDate>(type: "date", nullable: false),
                    PeriodEnd = table.Column<LocalDate>(type: "date", nullable: false),
                    InsightId = table.Column<Guid>(type: "uuid", nullable: false),
                    ThresholdGbp = table.Column<decimal>(type: "numeric", nullable: false),
                    ActualSpendGbp = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: false),
                    EmailAttemptedAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                    EmailSentAt = table.Column<Instant>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetAlertClaims", x => x.Id);
                    table.CheckConstraint("CK_BudgetAlertClaim_Period", "\"PeriodEnd\" >= \"PeriodStart\"");
                    table.ForeignKey(
                        name: "FK_BudgetAlertClaims_BudgetRules_BudgetRuleId",
                        column: x => x.BudgetRuleId,
                        principalTable: "BudgetRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade
                    );
                    table.ForeignKey(
                        name: "FK_BudgetAlertClaims_Insights_InsightId",
                        column: x => x.InsightId,
                        principalTable: "Insights",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict
                    );
                }
            );

            migrationBuilder.CreateIndex(
                name: "IX_BudgetAlertClaims_InsightId",
                table: "BudgetAlertClaims",
                column: "InsightId",
                unique: true
            );

            migrationBuilder.CreateIndex(
                name: "UX_BudgetAlertClaims_RulePeriod",
                table: "BudgetAlertClaims",
                columns: new[] { "BudgetRuleId", "PeriodStart", "PeriodEnd" },
                unique: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BudgetAlertClaims");
        }
    }
}
