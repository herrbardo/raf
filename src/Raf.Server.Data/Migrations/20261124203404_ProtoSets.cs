using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raf.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProtoSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProtoSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Files = table.Column<string>(type: "jsonb", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ChangeSeq = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProtoSets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProtoSets_WorkspaceId_ChangeSeq",
                table: "ProtoSets",
                columns: new[] { "WorkspaceId", "ChangeSeq" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProtoSets");
        }
    }
}
