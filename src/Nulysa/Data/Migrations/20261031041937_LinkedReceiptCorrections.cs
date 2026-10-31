using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class LinkedReceiptCorrections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Movement_Receipt",
            table: "MoneyMovements");

        migrationBuilder.AddColumn<long>(
            name: "ReversesMovementId",
            table: "MoneyMovements",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_ReversesMovementId",
            table: "MoneyMovements",
            column: "ReversesMovementId",
            unique: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Movement_Receipt",
            table: "MoneyMovements",
            sql: "(\"Kind\" IN ('ReceiptToLine','ReceiptToBilling','ReceiptToPurchase','BillingReceiptReversal','PurchaseReceiptReversal')) = (\"ReceiptId\" IS NOT NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Movement_Reversal",
            table: "MoneyMovements",
            sql: "(\"Kind\" IN ('BillingReceiptReversal','PurchaseReceiptReversal')) = (\"ReversesMovementId\" IS NOT NULL)");

        migrationBuilder.AddForeignKey(
            name: "FK_MoneyMovements_MoneyMovements_ReversesMovementId",
            table: "MoneyMovements",
            column: "ReversesMovementId",
            principalTable: "MoneyMovements",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_MoneyMovements_MoneyMovements_ReversesMovementId",
            table: "MoneyMovements");

        migrationBuilder.DropIndex(
            name: "IX_MoneyMovements_ReversesMovementId",
            table: "MoneyMovements");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Movement_Receipt",
            table: "MoneyMovements");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Movement_Reversal",
            table: "MoneyMovements");

        migrationBuilder.DropColumn(
            name: "ReversesMovementId",
            table: "MoneyMovements");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Movement_Receipt",
            table: "MoneyMovements",
            sql: "(\"Kind\" IN ('ReceiptToLine','ReceiptToBilling','ReceiptToPurchase')) = (\"ReceiptId\" IS NOT NULL)");
    }
}
