using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropIssuesAcceptedUpperBound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AdversarialReviewRun_IssuesAccepted_Valid",
                table: "AdversarialReviewRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdversarialReviewRun_IssuesAccepted_NonNegative",
                table: "AdversarialReviewRuns",
                sql: "\"IssuesAccepted\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_AdversarialReviewRun_IssuesAccepted_NonNegative",
                table: "AdversarialReviewRuns");

            migrationBuilder.AddCheckConstraint(
                name: "CK_AdversarialReviewRun_IssuesAccepted_Valid",
                table: "AdversarialReviewRuns",
                sql: "\"IssuesAccepted\" >= 0 AND \"IssuesAccepted\" <= \"IssuesRaised\"");
        }
    }
}
