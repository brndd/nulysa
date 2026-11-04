using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class EnableBankingConnections : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "BankConnections",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Bank = table.Column<string>(type: "text", nullable: false),
                IdentificationHash = table.Column<string>(type: "text", nullable: false),
                Label = table.Column<string>(type: "text", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                AccountUid = table.Column<Guid>(type: "uuid", nullable: false),
                ImportFrom = table.Column<DateOnly>(type: "date", nullable: false),
                ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiryReminderSent = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BankConnections", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BankConsentAttempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AdministratorId = table.Column<long>(type: "bigint", nullable: false),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                Bank = table.Column<string>(type: "text", nullable: false),
                ImportFrom = table.Column<DateOnly>(type: "date", nullable: false),
                RenewalId = table.Column<Guid>(type: "uuid", nullable: true),
                PreviousSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CallbackClaimed = table.Column<bool>(type: "boolean", nullable: false),
                SessionId = table.Column<Guid>(type: "uuid", nullable: true),
                ValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AccountChoices = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BankConsentAttempts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_BankConnections_Bank_IdentificationHash",
            table: "BankConnections",
            columns: new[] { "Bank", "IdentificationHash" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BankConsentAttempts_ExpiresAt",
            table: "BankConsentAttempts",
            column: "ExpiresAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "BankConnections");

        migrationBuilder.DropTable(
            name: "BankConsentAttempts");
    }
}
