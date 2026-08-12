using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUsageEventTelemetryIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UsageEvent_CostUsd_NonNegative",
                table: "UsageEvents");

            migrationBuilder.AlterColumn<decimal>(
                name: "CostUsd",
                table: "UsageEvents",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AddColumn<string>(
                name: "AgentId",
                table: "UsageEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Runtime",
                table: "UsageEvents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "UsageEvents",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ThoughtTokens",
                table: "UsageEvents",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_UsageEvent_CostUsd_NonNegative",
                table: "UsageEvents",
                sql: "\"CostUsd\" IS NULL OR \"CostUsd\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UsageEvent_ThoughtTokens_NonNegative",
                table: "UsageEvents",
                sql: "\"ThoughtTokens\" IS NULL OR \"ThoughtTokens\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UsageEvent_CostUsd_NonNegative",
                table: "UsageEvents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_UsageEvent_ThoughtTokens_NonNegative",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "Runtime",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "ThoughtTokens",
                table: "UsageEvents");

            migrationBuilder.AlterColumn<decimal>(
                name: "CostUsd",
                table: "UsageEvents",
                type: "numeric",
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_UsageEvent_CostUsd_NonNegative",
                table: "UsageEvents",
                sql: "\"CostUsd\" >= 0");
        }
    }
}
