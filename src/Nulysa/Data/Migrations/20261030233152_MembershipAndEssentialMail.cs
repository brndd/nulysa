using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class MembershipAndEssentialMail : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateOnly>(
            name: "DateOfBirth",
            table: "AspNetUsers",
            type: "date",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Language",
            table: "AspNetUsers",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "MatrixId",
            table: "AspNetUsers",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Municipality",
            table: "AspNetUsers",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "Nickname",
            table: "AspNetUsers",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "PendingEmail",
            table: "AspNetUsers",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "PendingEmailRequestedAt",
            table: "AspNetUsers",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "PendingEmailVersion",
            table: "AspNetUsers",
            type: "uuid",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "Applications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Municipality = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                PhoneNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                Nickname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                MatrixId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                TermsAcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                VerificationRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                VerificationVersion = table.Column<Guid>(type: "uuid", nullable: false),
                VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RejectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                RejectionExplanation = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Applications", x => x.Id);
                table.CheckConstraint("CK_Application_Decision", "\"RejectedAt\" IS NULL OR \"VerifiedAt\" IS NOT NULL");
            });

        migrationBuilder.CreateTable(
            name: "Approvals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                AdministratorId = table.Column<long>(type: "bigint", nullable: false),
                ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Approvals", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "BillingAccounts",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<long>(type: "bigint", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BillingAccounts", x => x.Id);
                table.ForeignKey(
                    name: "FK_BillingAccounts_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "LifecycleActions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                ActorId = table.Column<long>(type: "bigint", nullable: false),
                Action = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LifecycleActions", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "MailRateWindows",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Attempts = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MailRateWindows", x => x.Id);
                table.CheckConstraint("CK_MailRateWindow", "\"Id\" = 1 AND \"Attempts\" >= 0");
            });

        migrationBuilder.CreateTable(
            name: "Members",
            columns: table => new
            {
                UserId = table.Column<long>(type: "bigint", nullable: false),
                JoinedOn = table.Column<DateOnly>(type: "date", nullable: false),
                DeletionEligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeletionOperation = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Members", x => x.UserId);
                table.CheckConstraint("CK_Member_Deletion", "(\"DeletionEligibleAt\" IS NULL) = (\"DeletionOperation\" IS NULL)");
                table.ForeignKey(
                    name: "FK_Members_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Deliveries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                UserId = table.Column<long>(type: "bigint", nullable: true),
                ApplicationId = table.Column<Guid>(type: "uuid", nullable: true),
                Version = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                ProviderId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Deliveries", x => x.Id);
                table.CheckConstraint("CK_Delivery_Acceptance", "\"State\" <> 'Accepted' OR \"AcceptedAt\" IS NOT NULL");
                table.CheckConstraint("CK_Delivery_Attempts", "\"Attempts\" >= 0");
                table.CheckConstraint("CK_Delivery_Origin", "\"UserId\" IS NULL OR \"ApplicationId\" IS NULL");
                table.ForeignKey(
                    name: "FK_Deliveries_Applications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalTable: "Applications",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_Deliveries_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "InitialMembershipRequests",
            columns: table => new
            {
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                Anchor = table.Column<DateOnly>(type: "date", nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InitialMembershipRequests", x => x.BillingAccountId);
                table.CheckConstraint("CK_InitialRequest_Cancellation", "\"CancelledAt\" IS NULL OR \"CancelledAt\" >= \"RequestedAt\"");
                table.ForeignKey(
                    name: "FK_InitialMembershipRequests_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Applications_CreatedAt",
            table: "Applications",
            column: "CreatedAt");

        migrationBuilder.CreateIndex(
            name: "IX_Applications_NormalizedEmail",
            table: "Applications",
            column: "NormalizedEmail",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Approvals_UserId",
            table: "Approvals",
            column: "UserId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BillingAccounts_UserId",
            table: "BillingAccounts",
            column: "UserId",
            unique: true,
            filter: "\"UserId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_ApplicationId",
            table: "Deliveries",
            column: "ApplicationId");

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_State_CreatedAt",
            table: "Deliveries",
            columns: new[] { "State", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_UserId",
            table: "Deliveries",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_LifecycleActions_UserId",
            table: "LifecycleActions",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Approvals");

        migrationBuilder.DropTable(
            name: "Deliveries");

        migrationBuilder.DropTable(
            name: "InitialMembershipRequests");

        migrationBuilder.DropTable(
            name: "LifecycleActions");

        migrationBuilder.DropTable(
            name: "MailRateWindows");

        migrationBuilder.DropTable(
            name: "Members");

        migrationBuilder.DropTable(
            name: "Applications");

        migrationBuilder.DropTable(
            name: "BillingAccounts");

        migrationBuilder.DropColumn(
            name: "DateOfBirth",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "Language",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "MatrixId",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "Municipality",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "Nickname",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "PendingEmail",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "PendingEmailRequestedAt",
            table: "AspNetUsers");

        migrationBuilder.DropColumn(
            name: "PendingEmailVersion",
            table: "AspNetUsers");
    }
}
