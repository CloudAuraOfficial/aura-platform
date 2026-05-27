using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aura.Infrastructure.Migrations2
{
    /// <summary>
    /// Snapshot-only cleanup migration — Up/Down are intentionally empty.
    ///
    /// The prior model snapshot reflected an uncommitted "Experiment :
    /// TenantScopedEntity" change plus its companion migration (AddExperimentTenantId)
    /// that were never tracked in git and never applied to any deployed database.
    /// Reverting the tracked-code references to Experiment.TenantId regenerates
    /// the snapshot correctly, but EF auto-generates DROP COLUMN / DROP INDEX /
    /// DROP FK statements for columns and indexes that don't exist anywhere
    /// — those would crash on any real deploy.
    ///
    /// Empty Up/Down records this migration as applied in __EFMigrationsHistory
    /// so the snapshot stays consistent for future regenerations.
    /// </summary>
    public partial class CleanupOrphanArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: see class summary.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: see class summary.
        }
    }
}
