using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBudgetAlertEvaluationBoundaryAndEmailLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EmailAttemptedAt",
                table: "BudgetAlertClaims",
                newName: "EmailLeaseAcquiredAt"
            );

            migrationBuilder.AddColumn<LocalDate>(
                name: "EvaluationStartsOn",
                table: "BudgetRules",
                type: "date",
                nullable: false,
                defaultValueSql: "(CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date"
            );

            migrationBuilder.AddColumn<Guid>(
                name: "EmailLeaseId",
                table: "BudgetAlertClaims",
                type: "uuid",
                nullable: true
            );

            // A deployment that ran the preceding migration may already have in-flight
            // attempts. Preserve them as leases; their claim id is a stable one-time token
            // and the normal expiry path makes abandoned attempts retryable.
            migrationBuilder.Sql(
                """
                UPDATE "BudgetAlertClaims"
                SET "EmailLeaseId" = "Id"
                WHERE "EmailLeaseAcquiredAt" IS NOT NULL;
                """
            );

            migrationBuilder.AddCheckConstraint(
                name: "CK_BudgetAlertClaim_EmailLease",
                table: "BudgetAlertClaims",
                sql: "(\"EmailLeaseId\" IS NULL) = (\"EmailLeaseAcquiredAt\" IS NULL)"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_BudgetAlertClaim_EmailLease", table: "BudgetAlertClaims");

            migrationBuilder.DropColumn(name: "EvaluationStartsOn", table: "BudgetRules");

            migrationBuilder.DropColumn(name: "EmailLeaseId", table: "BudgetAlertClaims");

            migrationBuilder.RenameColumn(
                name: "EmailLeaseAcquiredAt",
                table: "BudgetAlertClaims",
                newName: "EmailAttemptedAt"
            );
        }
    }
}
