using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nulysa.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditLots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreditLots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                    ReceiptId = table.Column<long>(type: "bigint", nullable: false),
                    ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    OriginalAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RemainingAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditLots", x => x.Id);
                    table.CheckConstraint("CK_CreditLot_Amount", "\"OriginalAmount\" > 0 AND \"RemainingAmount\" >= 0 AND \"RemainingAmount\" <= \"OriginalAmount\"");
                    table.ForeignKey(
                        name: "FK_CreditLots_BillingAccounts_BillingAccountId",
                        column: x => x.BillingAccountId,
                        principalTable: "BillingAccounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditLots_Receipts_ReceiptId",
                        column: x => x.ReceiptId,
                        principalTable: "Receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditLots_BillingAccountId_ReceivedOn",
                table: "CreditLots",
                columns: new[] { "BillingAccountId", "ReceivedOn" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditLots_ReceiptId",
                table: "CreditLots",
                column: "ReceiptId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditLots");
        }
    }
}
