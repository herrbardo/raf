using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Raf.Server.Data.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// Las variables vuelven a valer para todas las Apps (Ticket-044, deja sin efecto el Plan 11). Antes de borrar la
    /// columna, cada variable de una App pasa a "Todas". Si en el mismo environment (o en las globales) queda más de una
    /// con el mismo nombre, se conserva la de "Todas" o, si no hay, la modificada más recientemente; las demás se marcan
    /// como borradas (siguen en la tabla). Lo que cambia lleva un ChangeSeq nuevo de su workspace, así los clientes lo
    /// reciben al sincronizar.
    /// </summary>
    public partial class SharedVariables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT "Id", "WorkspaceId", "AppId",
                           ROW_NUMBER() OVER (
                               PARTITION BY "WorkspaceId", "EnvironmentId", "Key"
                               ORDER BY ("AppId" IS NULL) DESC, "UpdatedAt" DESC, "Id") AS rank
                    FROM "Variables"
                    WHERE NOT "IsDeleted"
                ),
                touched AS (
                    SELECT "Id", "WorkspaceId", rank > 1 AS loses
                    FROM ranked
                    WHERE rank > 1 OR "AppId" IS NOT NULL
                ),
                sequences AS (
                    INSERT INTO "WorkspaceSequences" ("WorkspaceId", "Value")
                    SELECT DISTINCT "WorkspaceId", 1 FROM touched
                    ON CONFLICT ("WorkspaceId") DO UPDATE SET "Value" = "WorkspaceSequences"."Value" + 1
                    RETURNING "WorkspaceId", "Value"
                )
                UPDATE "Variables" AS v
                SET "AppId" = NULL,
                    "IsDeleted" = t.loses,
                    "Version" = v."Version" + 1,
                    "ChangeSeq" = s."Value",
                    "UpdatedAt" = now()
                FROM touched AS t
                JOIN sequences AS s ON s."WorkspaceId" = t."WorkspaceId"
                WHERE v."Id" = t."Id";
                """);

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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
    }
}
