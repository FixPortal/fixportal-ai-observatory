using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddObservationProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CacheSavingsUsd",
                table: "UsageEvents",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CostBasis",
                table: "UsageEvents",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<Instant>(
                name: "ObservedAt",
                table: "UsageEvents",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L));

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "UsageEvents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "legacy-api");

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "UsageEvents",
                type: "text",
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<string>(
                name: "UsageScope",
                table: "UsageEvents",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "CostBasis",
                table: "SpendEntries",
                type: "text",
                nullable: false,
                defaultValue: "Billed");

            migrationBuilder.AddColumn<Instant>(
                name: "ObservedAt",
                table: "SpendEntries",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: NodaTime.Instant.FromUnixTimeTicks(0L));

            migrationBuilder.AddColumn<string>(
                name: "RawPayload",
                table: "SpendEntries",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "SpendEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "legacy-spend");

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "SpendEntries",
                type: "text",
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<string>(
                name: "UsageScope",
                table: "SpendEntries",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "SourceId",
                table: "DailyAggregates",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "legacy-api");

            migrationBuilder.AddColumn<string>(
                name: "SourceKind",
                table: "DailyAggregates",
                type: "text",
                nullable: false,
                defaultValue: "Legacy");

            migrationBuilder.AddColumn<string>(
                name: "UsageScope",
                table: "DailyAggregates",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "CostBasis",
                table: "DailyAggregates",
                type: "text",
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<decimal>(
                name: "CacheSavingsUsd",
                table: "DailyAggregates",
                type: "numeric",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<int>(
                name: "UnknownCacheSavingsCount",
                table: "DailyAggregates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                """
                UPDATE "UsageEvents"
                SET "ObservedAt" = "IngestedAt"
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE "SpendEntries"
                SET "ObservedAt" = "RecordedAt"
                """
            );

            migrationBuilder.Sql(
                """
                UPDATE "DailyAggregates"
                SET "UnknownCacheSavingsCount" = "RequestCount"
                """
            );

            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_Provider_EventKey",
                table: "UsageEvents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DailyAggregates",
                table: "DailyAggregates");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DailyAggregates",
                table: "DailyAggregates",
                columns: new[] { "Date", "Provider", "Model", "SourceId", "SourceKind", "UsageScope", "CostBasis" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_SourceId_EventKey",
                table: "UsageEvents",
                columns: new[] { "SourceId", "EventKey" },
                unique: true,
                filter: "\"EventKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UsageEvents_SourceId_EventKey",
                table: "UsageEvents");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DailyAggregates",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "CacheSavingsUsd",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "CostBasis",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "ObservedAt",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "UsageScope",
                table: "UsageEvents");

            migrationBuilder.DropColumn(
                name: "CostBasis",
                table: "SpendEntries");

            migrationBuilder.DropColumn(
                name: "ObservedAt",
                table: "SpendEntries");

            migrationBuilder.DropColumn(
                name: "RawPayload",
                table: "SpendEntries");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "SpendEntries");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "SpendEntries");

            migrationBuilder.DropColumn(
                name: "UsageScope",
                table: "SpendEntries");

            migrationBuilder.DropColumn(
                name: "SourceId",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "SourceKind",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "UsageScope",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "CostBasis",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "CacheSavingsUsd",
                table: "DailyAggregates");

            migrationBuilder.DropColumn(
                name: "UnknownCacheSavingsCount",
                table: "DailyAggregates");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DailyAggregates",
                table: "DailyAggregates",
                columns: new[] { "Date", "Provider", "Model" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageEvents_Provider_EventKey",
                table: "UsageEvents",
                columns: new[] { "Provider", "EventKey" },
                unique: true,
                filter: "\"EventKey\" IS NOT NULL");
        }
    }
}
