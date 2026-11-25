using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raf.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class VariableApps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Variables_WorkspaceId_EnvironmentId_Key",
                table: "Variables");

            migrationBuilder.AddColumn<Guid>(
                name: "AppId",
                table: "Variables",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Variables_WorkspaceId_EnvironmentId_AppId_Key",
                table: "Variables",
                columns: new[] { "WorkspaceId", "EnvironmentId", "AppId", "Key" },
                unique: true,
                filter: "\"IsDeleted\" = false")
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Variables_WorkspaceId_EnvironmentId_AppId_Key",
                table: "Variables");

            migrationBuilder.DropColumn(
                name: "AppId",
                table: "Variables");

            migrationBuilder.CreateIndex(
                name: "IX_Variables_WorkspaceId_EnvironmentId_Key",
                table: "Variables",
                columns: new[] { "WorkspaceId", "EnvironmentId", "Key" },
                unique: true,
                filter: "\"IsDeleted\" = false")
                .Annotation("Npgsql:NullsDistinct", false);
        }
    }
}
