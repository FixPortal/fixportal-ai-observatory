using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUnknownCostCoverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UnknownCostCount",
                table: "DailyAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_UnknownCostCount_NonNegative",
                table: "DailyAggregates",
                sql: "\"UnknownCostCount\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_UnknownCostCount_NonNegative",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "UnknownCostCount",
                table: "DailyAggregates");
        }
    }
}
