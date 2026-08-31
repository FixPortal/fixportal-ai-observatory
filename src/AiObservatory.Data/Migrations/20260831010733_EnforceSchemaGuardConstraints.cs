using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSchemaGuardConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "CK_Subscription_BillingDay_Valid",
                table: "Subscriptions",
                sql: "\"BillingDay\" BETWEEN 1 AND 31"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_SpendEntry_Currency_Normalized",
                table: "SpendEntries",
                sql: "\"Currency\" ~ '^[A-Z]{3}$'"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationSettings_Singleton",
                table: "NotificationSettings",
                sql: "\"Id\" = '33333333-3333-3333-3333-333333333301'"
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_DailyAggregate_UnknownCacheSavingsCount_NonNegative",
                table: "DailyAggregates",
                sql: "\"UnknownCacheSavingsCount\" >= 0"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_Subscription_BillingDay_Valid", table: "Subscriptions");

            migrationBuilder.DropCheckConstraint(name: "CK_SpendEntry_Currency_Normalized", table: "SpendEntries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_NotificationSettings_Singleton",
                table: "NotificationSettings"
            );

            migrationBuilder.DropCheckConstraint(
                name: "CK_DailyAggregate_UnknownCacheSavingsCount_NonNegative",
                table: "DailyAggregates"
            );
        }
    }
}
