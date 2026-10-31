using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class BillingMovementEffectiveIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_EffectiveOn",
            table: "MoneyMovements",
            column: "EffectiveOn");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_MoneyMovements_EffectiveOn",
            table: "MoneyMovements");
    }
}
