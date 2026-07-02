using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compass.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyWorkReporting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectWeeklyWorkUpdates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    IsoYear = table.Column<int>(type: "int", nullable: false),
                    IsoWeek = table.Column<int>(type: "int", nullable: false),
                    WeekStartDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WeekEndDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Narrative = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    CreatedByEntraId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedByName = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedByEmail = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true),
                    UpdatedByUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    WeeklyPermFte = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    WeeklyMspFte = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    PeopleNarrative = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DraftRagStatusLookupId = table.Column<int>(type: "int", nullable: true),
                    DraftRagJustification = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    DraftPathToGreen = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectWeeklyWorkUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectWeeklyWorkUpdates_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ProjectWeeklyWorkUpdates_RagStatusLookups_DraftRagStatusLookupId",
                        column: x => x.DraftRagStatusLookupId,
                        principalTable: "RagStatusLookups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectWeeklyWorkUpdates_Users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ProjectWeeklyWorkUpdates_Users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WeeklyWorkReportingConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PeriodStartDayOfWeek = table.Column<int>(type: "int", nullable: false),
                    PeriodEndDayOfWeek = table.Column<int>(type: "int", nullable: false),
                    DueDayOfWeek = table.Column<int>(type: "int", nullable: false),
                    DueWeekOffset = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyWorkReportingConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyWorkReportingScopeProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProjectId = table.Column<int>(type: "int", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AddedByEmail = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyWorkReportingScopeProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyWorkReportingScopeProjects_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "WeeklyWorkReportingConfigs",
                columns: new[] { "Id", "DueDayOfWeek", "DueWeekOffset", "IsActive", "PeriodEndDayOfWeek", "PeriodStartDayOfWeek", "UpdatedAt" },
                values: new object[] { 1, 5, 0, true, 5, 1, new DateTime(2026, 7, 2, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWeeklyWorkUpdates_CreatedByUserId",
                table: "ProjectWeeklyWorkUpdates",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWeeklyWorkUpdates_DraftRagStatusLookupId",
                table: "ProjectWeeklyWorkUpdates",
                column: "DraftRagStatusLookupId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWeeklyWorkUpdates_ProjectId_IsoYear_IsoWeek",
                table: "ProjectWeeklyWorkUpdates",
                columns: new[] { "ProjectId", "IsoYear", "IsoWeek" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectWeeklyWorkUpdates_UpdatedByUserId",
                table: "ProjectWeeklyWorkUpdates",
                column: "UpdatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyWorkReportingScopeProjects_ProjectId",
                table: "WeeklyWorkReportingScopeProjects",
                column: "ProjectId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectWeeklyWorkUpdates");

            migrationBuilder.DropTable(
                name: "WeeklyWorkReportingConfigs");

            migrationBuilder.DropTable(
                name: "WeeklyWorkReportingScopeProjects");
        }
    }
}
