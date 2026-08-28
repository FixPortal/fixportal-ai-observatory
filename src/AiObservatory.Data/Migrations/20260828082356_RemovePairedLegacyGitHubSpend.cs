using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiObservatory.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemovePairedLegacyGitHubSpend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DELETE FROM "SpendEntries" AS legacy
                WHERE legacy."Source" = 'Api'
                  AND legacy."SourceId" = 'legacy-spend'
                  AND legacy."EntryKey" LIKE 'github:%'
                  AND EXISTS (
                      SELECT 1
                      FROM "SpendEntries" AS canonical
                      WHERE canonical."Source" = 'Api'
                        AND canonical."SourceId" = 'github-billing-api'
                        AND canonical."EntryKey" = 'billing:github-billing-api:' || legacy."EntryKey"
                  );
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Removed rows are stale snapshots with canonical retained-observation
            // counterparts. Recreating them would restore the financial double-count.
        }
    }
}
