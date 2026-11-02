using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gamertab.Data.Migrations
{
    public partial class AddCollectionCovers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CoverBlobName",
                table: "Collections",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoverBlobName",
                table: "Collections");
        }
    }
}
