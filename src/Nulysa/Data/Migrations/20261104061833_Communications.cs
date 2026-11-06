using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class Communications : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttemptLimit",
            table: "Deliveries",
            type: "integer",
            nullable: false,
            defaultValue: 3);

        migrationBuilder.AddColumn<Guid>(
            name: "CampaignId",
            table: "Deliveries",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "OutgoingLanguage",
            table: "Deliveries",
            type: "character varying(10)",
            maxLength: 10,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PreferredLanguage",
            table: "Deliveries",
            type: "character varying(10)",
            maxLength: 10,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProtectedTestMessage",
            table: "Deliveries",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "QueueVersion",
            table: "Deliveries",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<bool>(
            name: "EmailNewsletters",
            table: "AspNetUsers",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateTable(
            name: "MailCampaigns",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Revision = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Content = table.Column<string>(type: "jsonb", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                QueuedBy = table.Column<long>(type: "bigint", nullable: true),
                RecipientCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MailCampaigns", x => x.Id);
                table.CheckConstraint("CK_MailCampaign_Queued", "(\"QueuedAt\" IS NULL AND \"QueuedBy\" IS NULL AND \"RecipientCount\" = 0)\nOR (\"QueuedAt\" IS NOT NULL AND \"QueuedBy\" IS NOT NULL AND \"RecipientCount\" > 0)");
            });

        migrationBuilder.CreateTable(
            name: "MailDeliveryRetries",
            columns: table => new
            {
                DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                QueueVersion = table.Column<int>(type: "integer", nullable: false),
                ActorId = table.Column<long>(type: "bigint", nullable: false),
                Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MailDeliveryRetries", x => new { x.DeliveryId, x.QueueVersion });
                table.ForeignKey(
                    name: "FK_MailDeliveryRetries_Deliveries_DeliveryId",
                    column: x => x.DeliveryId,
                    principalTable: "Deliveries",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "OperationalMailTemplates",
            columns: table => new
            {
                Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                Content = table.Column<string>(type: "jsonb", nullable: false),
                FallbackLanguage = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedBy = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationalMailTemplates", x => x.Kind);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_CampaignId_State_CreatedAt",
            table: "Deliveries",
            columns: new[] { "CampaignId", "State", "CreatedAt" });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Delivery_Campaign",
            table: "Deliveries",
            sql: "(\"CampaignId\" IS NOT NULL) = (\"Kind\" IN ('Newsletter', 'OfficialNotice', 'CampaignTest'))\nAND (\"OutgoingLanguage\" IS NOT NULL) = (\"CampaignId\" IS NOT NULL)\nAND (\"PreferredLanguage\" IS NOT NULL) = (\"Kind\" IN ('Newsletter', 'OfficialNotice'))\nAND (\"ProtectedTestMessage\" IS NULL OR \"Kind\" = 'CampaignTest')\nAND (\"Kind\" <> 'CampaignTest' OR \"UserId\" IS NULL)");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Delivery_Queue",
            table: "Deliveries",
            sql: " \"QueueVersion\" >= 0 AND \"AttemptLimit\" >= \"Attempts\" ");

        migrationBuilder.CreateIndex(
            name: "IX_MailCampaigns_UpdatedAt",
            table: "MailCampaigns",
            column: "UpdatedAt");

        migrationBuilder.AddForeignKey(
            name: "FK_Deliveries_MailCampaigns_CampaignId",
            table: "Deliveries",
            column: "CampaignId",
            principalTable: "MailCampaigns",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Deliveries_MailCampaigns_CampaignId",
            table: "Deliveries");

        migrationBuilder.DropTable(
            name: "MailCampaigns");

        migrationBuilder.DropTable(
            name: "MailDeliveryRetries");

        migrationBuilder.DropTable(
            name: "OperationalMailTemplates");

        migrationBuilder.DropIndex(
            name: "IX_Deliveries_CampaignId_State_CreatedAt",
            table: "Deliveries");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Delivery_Campaign",
            table: "Deliveries");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Delivery_Queue",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "AttemptLimit",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "CampaignId",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "OutgoingLanguage",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "PreferredLanguage",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "ProtectedTestMessage",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "QueueVersion",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "EmailNewsletters",
            table: "AspNetUsers");
    }
}
