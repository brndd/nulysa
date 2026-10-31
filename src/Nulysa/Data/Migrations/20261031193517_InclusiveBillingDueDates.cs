using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class InclusiveBillingDueDates : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            UPDATE "InvoiceLines"
            SET "ExpiresOn" = "ChargeDueOn" + 1
            WHERE "State" = 'Open' AND "ExpiresOn" <= "ChargeDueOn";
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Do not shorten an issued payment window on downgrade.
    }
}
