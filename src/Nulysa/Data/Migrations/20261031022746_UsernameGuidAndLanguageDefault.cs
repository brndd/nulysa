using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class UsernameGuidAndLanguageDefault : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Existing rows predate the opaque username; give every row one stable GUID handle.
        migrationBuilder.Sql("""
                UPDATE "AspNetUsers"
                SET "UserName" = x.new_username, "NormalizedUserName" = upper(x.new_username)
                FROM (SELECT "Id" AS user_id, replace(gen_random_uuid()::text, '-', '') AS new_username FROM "AspNetUsers") AS x
                WHERE "AspNetUsers"."Id" = x.user_id;
                """);
        migrationBuilder.Sql("""UPDATE "AspNetUsers" SET "Language" = 'en' WHERE "Language" = '';""");

        migrationBuilder.AlterColumn<string>(
            name: "UserName",
            table: "AspNetUsers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256,
            oldNullable: true);

        migrationBuilder.AlterColumn<string>(
            name: "Language",
            table: "AspNetUsers",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            defaultValue: "en",
            oldClrType: typeof(string),
            oldType: "character varying(10)",
            oldMaxLength: 10);

        // The username is an internal immutable handle; no user journey or admin flow may change it.
        migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION nulysa_username_immutable() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW."UserName" IS DISTINCT FROM OLD."UserName"
                       OR NEW."NormalizedUserName" IS DISTINCT FROM OLD."NormalizedUserName" THEN
                        RAISE EXCEPTION 'UserName is immutable';
                    END IF;
                    RETURN NEW;
                END
                $$;

                DROP TRIGGER IF EXISTS "AspNetUsers_UserName_Immutable" ON "AspNetUsers";
                CREATE TRIGGER "AspNetUsers_UserName_Immutable"
                    BEFORE UPDATE ON "AspNetUsers"
                    FOR EACH ROW EXECUTE FUNCTION nulysa_username_immutable();
                """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "AspNetUsers_UserName_Immutable" ON "AspNetUsers"; DROP FUNCTION IF EXISTS nulysa_username_immutable();""");

        migrationBuilder.AlterColumn<string>(
            name: "UserName",
            table: "AspNetUsers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true,
            oldClrType: typeof(string),
            oldType: "character varying(256)",
            oldMaxLength: 256);

        migrationBuilder.AlterColumn<string>(
            name: "Language",
            table: "AspNetUsers",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(10)",
            oldMaxLength: 10,
            oldDefaultValue: "en");
    }
}
