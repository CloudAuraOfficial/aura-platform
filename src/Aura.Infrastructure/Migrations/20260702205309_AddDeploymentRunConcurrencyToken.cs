using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aura.Infrastructure.Migrations
{
    /// <summary>
    /// Enables optimistic concurrency on DeploymentRun via Postgres' xmin system
    /// column (see AuraDbContext.UseXminAsConcurrencyToken). No DDL: xmin already
    /// exists on every table as a system column — EF's generated AddColumn("xmin")
    /// would fail (42701, conflicts with a system column), so this migration is an
    /// intentional no-op that only advances the model snapshot.
    /// </summary>
    public partial class AddDeploymentRunConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
