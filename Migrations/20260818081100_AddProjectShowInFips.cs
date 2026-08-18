using Compass.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Compass.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(CompassDbContext))]
    [Migration("20260818081100_AddProjectShowInFips")]
    public partial class AddProjectShowInFips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowInFips",
                table: "Projects",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_ShowInFips",
                table: "Projects",
                column: "ShowInFips");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_ShowInFips",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "ShowInFips",
                table: "Projects");
        }
    }
}
