using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class FailureObservations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "FailureObservations",
            columns: table => new
            {
                Job = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                LastFailedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FailureObservations", x => x.Job);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "FailureObservations");
    }
}
