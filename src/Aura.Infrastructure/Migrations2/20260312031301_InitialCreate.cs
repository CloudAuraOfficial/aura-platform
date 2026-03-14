using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aura.Infrastructure.Migrations2
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Slug = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "text", nullable: false),
                    Label = table.Column<string>(type: "text", nullable: false),
                    EncryptedCredentials = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudAccounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CloudAccounts_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Essences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CloudAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EssenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Essences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Essences_CloudAccounts_CloudAccountId",
                        column: x => x.CloudAccountId,
                        principalTable: "CloudAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Essences_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Deployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EssenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    CronExpression = table.Column<string>(type: "text", nullable: true),
                    WebhookUrl = table.Column<string>(type: "text", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deployments_Essences_EssenceId",
                        column: x => x.EssenceId,
                        principalTable: "Essences",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Deployments_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentRuns_Deployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "Deployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeploymentRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentLayers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: false),
                    LayerName = table.Column<string>(type: "text", nullable: false),
                    ExecutorType = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: false),
                    ScriptPath = table.Column<string>(type: "text", nullable: true),
                    DependsOn = table.Column<string>(type: "jsonb", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Output = table.Column<string>(type: "text", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentLayers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentLayers_DeploymentRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "DeploymentRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CloudAccounts_TenantId",
                table: "CloudAccounts",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentLayers_RunId",
                table: "DeploymentLayers",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRuns_DeploymentId",
                table: "DeploymentRuns",
                column: "DeploymentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentRuns_TenantId",
                table: "DeploymentRuns",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_EssenceId",
                table: "Deployments",
                column: "EssenceId");

            migrationBuilder.CreateIndex(
                name: "IX_Deployments_TenantId",
                table: "Deployments",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Essences_CloudAccountId",
                table: "Essences",
                column: "CloudAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Essences_TenantId",
                table: "Essences",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_Email",
                table: "Users",
                columns: new[] { "TenantId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeploymentLayers");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "DeploymentRuns");

            migrationBuilder.DropTable(
                name: "Deployments");

            migrationBuilder.DropTable(
                name: "Essences");

            migrationBuilder.DropTable(
                name: "CloudAccounts");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}
