using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compass.Migrations
{
    /// <inheritdoc />
    public partial class AddWeeklyWorkReportingFirstPeriodStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstReportingPeriodStart",
                table: "WeeklyWorkReportingConfigs",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.UpdateData(
                table: "WeeklyWorkReportingConfigs",
                keyColumn: "Id",
                keyValue: 1,
                column: "FirstReportingPeriodStart",
                value: new DateTime(2026, 6, 29, 0, 0, 0, 0, DateTimeKind.Utc));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstReportingPeriodStart",
                table: "WeeklyWorkReportingConfigs");
        }
    }
}
