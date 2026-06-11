using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aura.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLayerRunPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RunPolicy",
                table: "DeploymentLayers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RunPolicy",
                table: "DeploymentLayers");
        }
    }
}
