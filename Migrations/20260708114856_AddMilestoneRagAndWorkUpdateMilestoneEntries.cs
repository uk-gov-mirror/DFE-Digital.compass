using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compass.Migrations
{
    /// <inheritdoc />
    public partial class AddMilestoneRagAndWorkUpdateMilestoneEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NewRagStatusLookupId",
                table: "MilestoneUpdates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousRagStatusLookupId",
                table: "MilestoneUpdates",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RagStatusLookupId",
                table: "Milestones",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkUpdateMilestoneEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    MilestoneId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    RagStatusLookupId = table.Column<int>(type: "int", nullable: true),
                    UpdateNote = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: true),
                    ProjectMonthlyUpdateId = table.Column<int>(type: "int", nullable: true),
                    ProjectWeeklyWorkUpdateId = table.Column<int>(type: "int", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkUpdateMilestoneEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkUpdateMilestoneEntries_Milestones_MilestoneId",
                        column: x => x.MilestoneId,
                        principalTable: "Milestones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkUpdateMilestoneEntries_ProjectMonthlyUpdates_ProjectMonthlyUpdateId",
                        column: x => x.ProjectMonthlyUpdateId,
                        principalTable: "ProjectMonthlyUpdates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkUpdateMilestoneEntries_ProjectWeeklyWorkUpdates_ProjectWeeklyWorkUpdateId",
                        column: x => x.ProjectWeeklyWorkUpdateId,
                        principalTable: "ProjectWeeklyWorkUpdates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkUpdateMilestoneEntries_RagStatusLookups_RagStatusLookupId",
                        column: x => x.RagStatusLookupId,
                        principalTable: "RagStatusLookups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Milestones_RagStatusLookupId",
                table: "Milestones",
                column: "RagStatusLookupId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUpdateMilestoneEntries_MilestoneId",
                table: "WorkUpdateMilestoneEntries",
                column: "MilestoneId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUpdateMilestoneEntries_ProjectMonthlyUpdateId",
                table: "WorkUpdateMilestoneEntries",
                column: "ProjectMonthlyUpdateId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUpdateMilestoneEntries_ProjectWeeklyWorkUpdateId",
                table: "WorkUpdateMilestoneEntries",
                column: "ProjectWeeklyWorkUpdateId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkUpdateMilestoneEntries_RagStatusLookupId",
                table: "WorkUpdateMilestoneEntries",
                column: "RagStatusLookupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Milestones_RagStatusLookups_RagStatusLookupId",
                table: "Milestones",
                column: "RagStatusLookupId",
                principalTable: "RagStatusLookups",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Milestones_RagStatusLookups_RagStatusLookupId",
                table: "Milestones");

            migrationBuilder.DropTable(
                name: "WorkUpdateMilestoneEntries");

            migrationBuilder.DropIndex(
                name: "IX_Milestones_RagStatusLookupId",
                table: "Milestones");

            migrationBuilder.DropColumn(
                name: "NewRagStatusLookupId",
                table: "MilestoneUpdates");

            migrationBuilder.DropColumn(
                name: "PreviousRagStatusLookupId",
                table: "MilestoneUpdates");

            migrationBuilder.DropColumn(
                name: "RagStatusLookupId",
                table: "Milestones");
        }
    }
}
