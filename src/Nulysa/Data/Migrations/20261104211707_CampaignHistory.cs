using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nulysa.Data.Migrations
{
    /// <inheritdoc />
    public partial class CampaignHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AcceptedRecipients",
                table: "MailCampaigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE "MailCampaigns" AS campaign
                SET "AcceptedRecipients" = accepted.count
                FROM (
                    SELECT "CampaignId", count(*)::integer AS count
                    FROM "Deliveries"
                    WHERE "CampaignId" IS NOT NULL AND "State" = 'Accepted' AND "Kind" <> 'CampaignTest'
                    GROUP BY "CampaignId"
                ) AS accepted
                WHERE campaign."Id" = accepted."CampaignId";
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_MailCampaign_Accepted",
                table: "MailCampaigns",
                sql: "\"AcceptedRecipients\" BETWEEN 0 AND \"RecipientCount\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_MailCampaign_Accepted",
                table: "MailCampaigns");

            migrationBuilder.DropColumn(
                name: "AcceptedRecipients",
                table: "MailCampaigns");
        }
    }
}
