using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raf.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class SharedLogins : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LoginId",
                table: "Apps",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Logins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Config = table.Column<string>(type: "jsonb", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    ChangeSeq = table.Column<long>(type: "bigint", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Logins", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Apps_LoginId",
                table: "Apps",
                column: "LoginId");

            migrationBuilder.CreateIndex(
                name: "IX_Logins_WorkspaceId_ChangeSeq",
                table: "Logins",
                columns: new[] { "WorkspaceId", "ChangeSeq" });

            // Cada App con login propio pasa a usar un login compartido con su nombre. Los cambios se numeran como
            // cualquier edición, para que los clientes reciban el login nuevo y la App actualizada.
            migrationBuilder.Sql(MoveLoginsSql);

            migrationBuilder.DropColumn(
                name: "Login",
                table: "Apps");
        }

        private const string MoveLoginsSql = """
            DO $$
            DECLARE
                app record;
                seq bigint;
                login_id uuid;
            BEGIN
                FOR app IN
                    SELECT "Id", "WorkspaceId", "Name", "Login" FROM "Apps"
                    WHERE "Login" IS NOT NULL AND NOT "IsDeleted"
                    ORDER BY "WorkspaceId", "Order", "Name"
                LOOP
                    INSERT INTO "WorkspaceSequences" ("WorkspaceId", "Value") VALUES (app."WorkspaceId", 1)
                    ON CONFLICT ("WorkspaceId") DO UPDATE SET "Value" = "WorkspaceSequences"."Value" + 1
                    RETURNING "Value" INTO seq;

                    login_id := gen_random_uuid();
                    INSERT INTO "Logins" ("Id", "Name", "Config", "WorkspaceId", "Version", "ChangeSeq", "IsDeleted", "UpdatedAt", "UpdatedBy")
                    VALUES (login_id, app."Name", app."Login", app."WorkspaceId", 1, seq, false, now(), NULL);

                    UPDATE "Apps"
                    SET "LoginId" = login_id, "Version" = "Version" + 1, "ChangeSeq" = seq, "UpdatedAt" = now()
                    WHERE "Id" = app."Id";
                END LOOP;
            END $$;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Login",
                table: "Apps",
                type: "jsonb",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Apps" AS a SET "Login" = l."Config"
                FROM "Logins" AS l
                WHERE a."LoginId" = l."Id"
                """);

            migrationBuilder.DropTable(
                name: "Logins");

            migrationBuilder.DropIndex(
                name: "IX_Apps_LoginId",
                table: "Apps");

            migrationBuilder.DropColumn(
                name: "LoginId",
                table: "Apps");
        }
    }
}
