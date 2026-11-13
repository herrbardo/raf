using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raf.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppAuth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Auth",
                table: "Apps",
                type: "jsonb",
                nullable: false,
                // Las apps existentes quedan sin autenticación (AuthSpec.None).
                defaultValue: "{\"type\":\"None\",\"apiKeyIn\":\"Header\"}");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Auth",
                table: "Apps");
        }
    }
}
