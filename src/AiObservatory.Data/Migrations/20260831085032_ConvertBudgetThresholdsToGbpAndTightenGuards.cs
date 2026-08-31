using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConvertBudgetThresholdsToGbpAndTightenGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // D2/NS-2: AddBudgetAlertsAndRenameThresholdToGbp renamed ThresholdUsd to
            // ThresholdGbp with no conversion, so a pre-existing rule entered in USD kept
            // its number as a GBP figure (~20% looser) from the first evaluation after
            // that deploy. Convert the stored values once, pinned to the ledger's
            // documented USD->GBP fallback rate (FxRateProvider, 0.79). Rows entered in
            // GBP between the rename and this migration cannot be distinguished and would
            // over-convert; on a fresh database this updates no rows at all.
            migrationBuilder.Sql(
                """
                UPDATE "BudgetRules"
                SET "ThresholdGbp" = round("ThresholdGbp" * 0.79, 2)
                """
            );

            // D7: the (CURRENT_TIMESTAMP ... )::date default doubled as the backfill for
            // pre-existing rules and then lingered on the column, silently stamping "today"
            // onto any later insert that omitted it. Existing rows keep their original
            // deploy-day start (re-backfilling now cannot tell those apart from values set
            // deliberately since); the default goes away so omissions fail instead.
            migrationBuilder.AlterColumn<LocalDate>(
                name: "EvaluationStartsOn",
                table: "BudgetRules",
                type: "date",
                nullable: false,
                oldClrType: typeof(LocalDate),
                oldType: "date",
                oldDefaultValueSql: "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_UsageEvent_NoneCostBasis_NoCost",
                table: "UsageEvents",
                sql: "\"CostBasis\" <> 'None' OR \"CostUsd\" IS NULL OR \"CostUsd\" = 0"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_BillingObservation_Credit_Sign",
                table: "BillingObservations",
                sql: "\"CreditAmount\" <= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_UsageEvent_NoneCostBasis_NoCost", table: "UsageEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_BillingObservation_Credit_Sign",
                table: "BillingObservations"
            );

            migrationBuilder.AlterColumn<LocalDate>(
                name: "EvaluationStartsOn",
                table: "BudgetRules",
                type: "date",
                nullable: false,
                defaultValueSql: "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date",
                oldClrType: typeof(LocalDate),
                oldType: "date"
            );
        }
    }
}
