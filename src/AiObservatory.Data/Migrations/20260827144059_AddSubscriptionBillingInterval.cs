using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionBillingInterval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillingInterval",
                table: "Subscriptions",
                type: "text",
                nullable: false,
                defaultValue: "Monthly"
            );

            migrationBuilder.AddColumn<int>(
                name: "BillingMonth",
                table: "Subscriptions",
                type: "integer",
                nullable: true
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "BillingInterval", table: "Subscriptions");

            migrationBuilder.DropColumn(name: "BillingMonth", table: "Subscriptions");
        }
    }
}
