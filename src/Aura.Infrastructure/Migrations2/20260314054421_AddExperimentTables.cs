using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aura.Infrastructure.Migrations2
{
    /// <inheritdoc />
    public partial class AddExperimentTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginAttempts",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "InviteToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InviteTokenExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntil",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefreshToken",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RefreshTokenExpiresAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CurrentVersion",
                table: "Essences",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AuditLog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Action = table.Column<string>(type: "text", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EssenceVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EssenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    EssenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EssenceVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EssenceVersions_Essences_EssenceId",
                        column: x => x.EssenceId,
                        principalTable: "Essences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Experiments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Project = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Hypothesis = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Variants = table.Column<string>(type: "jsonb", nullable: false),
                    MetricName = table.Column<string>(type: "text", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConcludedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Conclusion = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubjectHash = table.Column<string>(type: "text", nullable: false),
                    VariantId = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentAssignments_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "uuid", nullable: false),
                    VariantId = table.Column<string>(type: "text", nullable: false),
                    SubjectHash = table.Column<string>(type: "text", nullable: false),
                    MetricName = table.Column<string>(type: "text", nullable: false),
                    MetricValue = table.Column<double>(type: "double precision", nullable: false),
                    Metadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentEvents_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_InviteToken",
                table: "Users",
                column: "InviteToken",
                unique: true,
                filter: "\"InviteToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_CreatedAt",
                table: "AuditLog",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditLog_TenantId_EntityType_EntityId",
                table: "AuditLog",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_EssenceVersions_EssenceId_VersionNumber",
                table: "EssenceVersions",
                columns: new[] { "EssenceId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentAssignments_ExperimentId_SubjectHash",
                table: "ExperimentAssignments",
                columns: new[] { "ExperimentId", "SubjectHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentEvents_ExperimentId_VariantId",
                table: "ExperimentEvents",
                columns: new[] { "ExperimentId", "VariantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_Project_Status",
                table: "Experiments",
                columns: new[] { "Project", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditLog");

            migrationBuilder.DropTable(
                name: "EssenceVersions");

            migrationBuilder.DropTable(
                name: "ExperimentAssignments");

            migrationBuilder.DropTable(
                name: "ExperimentEvents");

            migrationBuilder.DropTable(
                name: "Experiments");

            migrationBuilder.DropIndex(
                name: "IX_Users_InviteToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "FailedLoginAttempts",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InviteToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "InviteTokenExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "IsDisabled",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RefreshToken",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RefreshTokenExpiresAt",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "CurrentVersion",
                table: "Essences");
        }
    }
}
