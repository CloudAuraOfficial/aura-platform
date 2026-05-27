using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aura.Infrastructure.Migrations
{
    /// <summary>
    /// Drops the Experiments.TenantId column and the
    /// IX_Experiments_TenantId_Project_Status composite index.
    ///
    /// History: the column + index landed via an orphan migration
    /// (AddExperimentTenantId) that was hand-applied to production but
    /// never tracked in git. Today's revert (commit 08bd2ee) removed all
    /// code references to Experiment.TenantId, leaving the column inert
    /// on prod. This migration brings the schema in line with the code.
    ///
    /// Idempotent (IF EXISTS guards) so it's safe on any DB state — does
    /// nothing on a fresh install that never had the orphan applied.
    /// </summary>
    public partial class DropExperimentTenantIdColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Experiments_TenantId_Project_Status"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Experiments"" DROP COLUMN IF EXISTS ""TenantId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"ALTER TABLE ""Experiments"" ADD COLUMN IF NOT EXISTS ""TenantId"" uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';");
            migrationBuilder.Sql(@"CREATE INDEX IF NOT EXISTS ""IX_Experiments_TenantId_Project_Status"" ON ""Experiments"" (""TenantId"", ""Project"", ""Status"");");
        }
    }
}
