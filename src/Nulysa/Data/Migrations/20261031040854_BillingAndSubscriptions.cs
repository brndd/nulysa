using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class BillingAndSubscriptions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "InvoiceId",
            table: "InitialMembershipRequests",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "InvoiceId",
            table: "Deliveries",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "InvoiceRevision",
            table: "Deliveries",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ClaimEmail",
            table: "BillingAccounts",
            type: "character varying(256)",
            maxLength: 256,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ClaimName",
            table: "BillingAccounts",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "EmailInvoices",
            table: "BillingAccounts",
            type: "boolean",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "PurchaseDepositReference",
            table: "BillingAccounts",
            type: "character varying(20)",
            maxLength: 20,
            nullable: false,
            defaultValue: "");

        // S1 accounts need distinct, checksum-valid references before the unique index is installed.
        migrationBuilder.Sql("""
            WITH bases AS (
              SELECT "Id", '4' || lpad("Id"::text, greatest(3, length("Id"::text)), '0') AS base FROM "BillingAccounts"
            ), checks AS (
              SELECT "Id", base, (10 - sum(substr(reverse(base), n, 1)::int * (ARRAY[7,3,1])[((n-1)%3)+1]) % 10) % 10 AS digit
              FROM bases CROSS JOIN LATERAL generate_series(1, length(base)) n GROUP BY "Id", base
            )
            UPDATE "BillingAccounts" a SET "PurchaseDepositReference" = checks.base || checks.digit::text, "EmailInvoices" = true
            FROM checks WHERE a."Id" = checks."Id";
            """);

        migrationBuilder.CreateTable(
            name: "BankSyncStates",
            columns: table => new
            {
                Account = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                LastAttempt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastSuccess = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastAlert = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BankSyncStates", x => x.Account);
            });

        migrationBuilder.CreateTable(
            name: "BillingGroups",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                IntervalMonths = table.Column<int>(type: "integer", nullable: false),
                Anchor = table.Column<DateOnly>(type: "date", nullable: false),
                PaymentReference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BillingGroups", x => x.Id);
                table.CheckConstraint("CK_Group_Interval", "\"IntervalMonths\" BETWEEN 1 AND 120");
                table.ForeignKey(
                    name: "FK_BillingGroups_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "BillingOperations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ActorId = table.Column<long>(type: "bigint", nullable: true),
                Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ResultId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BillingOperations", x => x.Id);
                table.ForeignKey(
                    name: "FK_BillingOperations_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Receipts",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Source = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                SourceAccount = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                ReceivedOn = table.Column<DateOnly>(type: "date", nullable: false),
                ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                Counterparty = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ReviewReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Receipts", x => x.Id);
                table.CheckConstraint("CK_Receipt_Amount", "\"Amount\" <> 0");
                table.ForeignKey(
                    name: "FK_Receipts_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ServiceDefinitions",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                SelfSubscriptionAllowed = table.Column<bool>(type: "boolean", nullable: false),
                InitialFreeDays = table.Column<int>(type: "integer", nullable: false),
                RenewalGraceDays = table.Column<int>(type: "integer", nullable: false),
                Priority = table.Column<int>(type: "integer", nullable: false),
                Retired = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServiceDefinitions", x => x.Id);
                table.CheckConstraint("CK_Service_Durations", "\"InitialFreeDays\" >= 0 AND \"RenewalGraceDays\" >= 0 AND \"Priority\" >= 0");
            });

        migrationBuilder.CreateTable(
            name: "Invoices",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                BillingGroupId = table.Column<long>(type: "bigint", nullable: true),
                OneOffReference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Revision = table.Column<int>(type: "integer", nullable: false),
                NotifiedRevision = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Invoices", x => x.Id);
                table.CheckConstraint("CK_Invoice_Routing", "(\"BillingGroupId\" IS NULL) <> (\"OneOffReference\" IS NULL)");
                table.ForeignKey(
                    name: "FK_Invoices_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Invoices_BillingGroups_BillingGroupId",
                    column: x => x.BillingGroupId,
                    principalTable: "BillingGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Fees",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ServiceDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                Retired = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Fees", x => x.Id);
                table.ForeignKey(
                    name: "FK_Fees_ServiceDefinitions_ServiceDefinitionId",
                    column: x => x.ServiceDefinitionId,
                    principalTable: "ServiceDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ServiceEnrollments",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                ServiceDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RenewalStoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EndReason = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                FirstPaidOn = table.Column<DateOnly>(type: "date", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServiceEnrollments", x => x.Id);
                table.ForeignKey(
                    name: "FK_ServiceEnrollments_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ServiceEnrollments_ServiceDefinitions_ServiceDefinitionId",
                    column: x => x.ServiceDefinitionId,
                    principalTable: "ServiceDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Discounts",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                FeeDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Value = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                WithdrawnAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Discounts", x => x.Id);
                table.CheckConstraint("CK_Discount", "\"StartsOn\" < \"EndsOn\" AND \"Value\" >= 0 AND (\"Kind\" <> 'Percentage' OR \"Value\" <= 100)");
                table.ForeignKey(
                    name: "FK_Discounts_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Discounts_BillingOperations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "BillingOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Discounts_Fees_FeeDefinitionId",
                    column: x => x.FeeDefinitionId,
                    principalTable: "Fees",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "FeePrices",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                FeeDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                MonthlyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_FeePrices", x => x.Id);
                table.CheckConstraint("CK_Price_Amount", "\"MonthlyAmount\" >= 0");
                table.ForeignKey(
                    name: "FK_FeePrices_Fees_FeeDefinitionId",
                    column: x => x.FeeDefinitionId,
                    principalTable: "Fees",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RecurringItems",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BillingGroupId = table.Column<long>(type: "bigint", nullable: false),
                FeeDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                MembershipId = table.Column<long>(type: "bigint", nullable: true),
                ServiceEnrollmentId = table.Column<long>(type: "bigint", nullable: true),
                StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                NextOn = table.Column<DateOnly>(type: "date", nullable: false),
                EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RecurringItems", x => x.Id);
                table.CheckConstraint("CK_Item_Owner", "(\"MembershipId\" IS NULL) <> (\"ServiceEnrollmentId\" IS NULL)");
                table.ForeignKey(
                    name: "FK_RecurringItems_BillingGroups_BillingGroupId",
                    column: x => x.BillingGroupId,
                    principalTable: "BillingGroups",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_RecurringItems_Fees_FeeDefinitionId",
                    column: x => x.FeeDefinitionId,
                    principalTable: "Fees",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_RecurringItems_Members_MembershipId",
                    column: x => x.MembershipId,
                    principalTable: "Members",
                    principalColumn: "UserId",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_RecurringItems_ServiceEnrollments_ServiceEnrollmentId",
                    column: x => x.ServiceEnrollmentId,
                    principalTable: "ServiceEnrollments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "ServiceChanges",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                ServiceEnrollmentId = table.Column<long>(type: "bigint", nullable: true),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServiceChanges", x => x.Id);
                table.ForeignKey(
                    name: "FK_ServiceChanges_BillingOperations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "BillingOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_ServiceChanges_ServiceEnrollments_ServiceEnrollmentId",
                    column: x => x.ServiceEnrollmentId,
                    principalTable: "ServiceEnrollments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "InvoiceLines",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                InvoiceId = table.Column<long>(type: "bigint", nullable: false),
                RecurringItemId = table.Column<long>(type: "bigint", nullable: true),
                ServiceEnrollmentId = table.Column<long>(type: "bigint", nullable: true),
                ServiceDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                FeeDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                Benefit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                PurchaseKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                Category = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                OriginalStartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                OriginalEndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                ChargeDueOn = table.Column<DateOnly>(type: "date", nullable: false),
                ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                PricingJson = table.Column<string>(type: "jsonb", nullable: false),
                OriginalPricingJson = table.Column<string>(type: "jsonb", nullable: false),
                State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                FulfilledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                EffectiveOn = table.Column<DateOnly>(type: "date", nullable: true),
                LastFundingOn = table.Column<DateOnly>(type: "date", nullable: true),
                GraceNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ExpiryNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_InvoiceLines", x => x.Id);
                table.CheckConstraint("CK_Line_Benefit", "(\"Benefit\" = 'Service') = (\"ServiceDefinitionId\" IS NOT NULL)");
                table.CheckConstraint("CK_Line_Fulfilment", "(\"State\" = 'Fulfilled') = (\"FulfilledAt\" IS NOT NULL)");
                table.CheckConstraint("CK_Line_PricePeriod", "\"Amount\" >= 0 AND \"StartsOn\" < \"EndsOn\" AND \"OriginalStartsOn\" < \"OriginalEndsOn\"");
                table.ForeignKey(
                    name: "FK_InvoiceLines_Fees_FeeDefinitionId",
                    column: x => x.FeeDefinitionId,
                    principalTable: "Fees",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_InvoiceLines_Invoices_InvoiceId",
                    column: x => x.InvoiceId,
                    principalTable: "Invoices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_InvoiceLines_RecurringItems_RecurringItemId",
                    column: x => x.RecurringItemId,
                    principalTable: "RecurringItems",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    name: "FK_InvoiceLines_ServiceDefinitions_ServiceDefinitionId",
                    column: x => x.ServiceDefinitionId,
                    principalTable: "ServiceDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_InvoiceLines_ServiceEnrollments_ServiceEnrollmentId",
                    column: x => x.ServiceEnrollmentId,
                    principalTable: "ServiceEnrollments",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.SetNull);
            });

        migrationBuilder.CreateTable(
            name: "Coverage",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                Benefit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ServiceDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                InvoiceLineId = table.Column<long>(type: "bigint", nullable: true),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                Origin = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Coverage", x => x.Id);
                table.CheckConstraint("CK_Coverage_Benefit", "\"Benefit\" IN ('Membership','Service') AND (\"Benefit\" = 'Service') = (\"ServiceDefinitionId\" IS NOT NULL)");
                table.CheckConstraint("CK_Coverage_Period", "\"StartsOn\" < \"EndsOn\"");
                table.CheckConstraint("CK_Coverage_Source", "(\"Source\" = 'Purchase') = (\"InvoiceLineId\" IS NOT NULL)");
                table.ForeignKey(
                    name: "FK_Coverage_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Coverage_BillingOperations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "BillingOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Coverage_InvoiceLines_InvoiceLineId",
                    column: x => x.InvoiceLineId,
                    principalTable: "InvoiceLines",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Coverage_ServiceDefinitions_ServiceDefinitionId",
                    column: x => x.ServiceDefinitionId,
                    principalTable: "ServiceDefinitions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "LineAdjustments",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                InvoiceLineId = table.Column<long>(type: "bigint", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                OldAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                NewAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                OldStart = table.Column<DateOnly>(type: "date", nullable: false),
                OldEnd = table.Column<DateOnly>(type: "date", nullable: false),
                NewStart = table.Column<DateOnly>(type: "date", nullable: false),
                NewEnd = table.Column<DateOnly>(type: "date", nullable: false),
                PricingJson = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_LineAdjustments", x => x.Id);
                table.ForeignKey(
                    name: "FK_LineAdjustments_BillingOperations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "BillingOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_LineAdjustments_InvoiceLines_InvoiceLineId",
                    column: x => x.InvoiceLineId,
                    principalTable: "InvoiceLines",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "MoneyMovements",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                EffectiveOn = table.Column<DateOnly>(type: "date", nullable: false),
                ReceiptId = table.Column<long>(type: "bigint", nullable: true),
                InvoiceLineId = table.Column<long>(type: "bigint", nullable: true),
                DiscountId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MoneyMovements", x => x.Id);
                table.CheckConstraint("CK_Movement_Amount", "\"Amount\" > 0");
                table.CheckConstraint("CK_Movement_Discount", "(\"Kind\" = 'PurchaseCredit') = (\"DiscountId\" IS NOT NULL)");
                table.CheckConstraint("CK_Movement_Line", "(\"Kind\" IN ('ReceiptToLine','BillingToLine','LineToBilling','LinePurchase','PurchaseCredit')) = (\"InvoiceLineId\" IS NOT NULL)");
                table.CheckConstraint("CK_Movement_Receipt", "(\"Kind\" IN ('ReceiptToLine','ReceiptToBilling','ReceiptToPurchase')) = (\"ReceiptId\" IS NOT NULL)");
                table.ForeignKey(
                    name: "FK_MoneyMovements_BillingAccounts_BillingAccountId",
                    column: x => x.BillingAccountId,
                    principalTable: "BillingAccounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MoneyMovements_BillingOperations_OperationId",
                    column: x => x.OperationId,
                    principalTable: "BillingOperations",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MoneyMovements_Discounts_DiscountId",
                    column: x => x.DiscountId,
                    principalTable: "Discounts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MoneyMovements_InvoiceLines_InvoiceLineId",
                    column: x => x.InvoiceLineId,
                    principalTable: "InvoiceLines",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_MoneyMovements_Receipts_ReceiptId",
                    column: x => x.ReceiptId,
                    principalTable: "Receipts",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.InsertData(
            table: "Fees",
            columns: new[] { "Id", "Category", "Name", "Retired", "ServiceDefinitionId" },
            values: new object[] { 1L, "membership", "Membership", false, null });

        migrationBuilder.CreateIndex(
            name: "IX_InitialMembershipRequests_InvoiceId",
            table: "InitialMembershipRequests",
            column: "InvoiceId");

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_InvoiceId",
            table: "Deliveries",
            column: "InvoiceId");

        migrationBuilder.CreateIndex(
            name: "IX_BillingAccounts_PurchaseDepositReference",
            table: "BillingAccounts",
            column: "PurchaseDepositReference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BillingGroups_BillingAccountId_IntervalMonths",
            table: "BillingGroups",
            columns: new[] { "BillingAccountId", "IntervalMonths" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BillingGroups_PaymentReference",
            table: "BillingGroups",
            column: "PaymentReference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BillingOperations_BillingAccountId",
            table: "BillingOperations",
            column: "BillingAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_Coverage_BillingAccountId_ServiceDefinitionId",
            table: "Coverage",
            columns: new[] { "BillingAccountId", "ServiceDefinitionId" },
            unique: true,
            filter: "\"Source\" = 'InitialFree'");

        migrationBuilder.CreateIndex(
            name: "IX_Coverage_InvoiceLineId",
            table: "Coverage",
            column: "InvoiceLineId");

        migrationBuilder.CreateIndex(
            name: "IX_Coverage_OperationId",
            table: "Coverage",
            column: "OperationId");

        migrationBuilder.CreateIndex(
            name: "IX_Coverage_Origin",
            table: "Coverage",
            column: "Origin",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Coverage_ServiceDefinitionId",
            table: "Coverage",
            column: "ServiceDefinitionId");

        migrationBuilder.CreateIndex(
            name: "IX_Discounts_BillingAccountId",
            table: "Discounts",
            column: "BillingAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_Discounts_FeeDefinitionId",
            table: "Discounts",
            column: "FeeDefinitionId");

        migrationBuilder.CreateIndex(
            name: "IX_Discounts_OperationId",
            table: "Discounts",
            column: "OperationId");

        migrationBuilder.CreateIndex(
            name: "IX_FeePrices_FeeDefinitionId_EffectiveOn",
            table: "FeePrices",
            columns: new[] { "FeeDefinitionId", "EffectiveOn" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Fees_ServiceDefinitionId",
            table: "Fees",
            column: "ServiceDefinitionId",
            unique: true)
            .Annotation("Npgsql:NullsDistinct", false);

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceLines_FeeDefinitionId",
            table: "InvoiceLines",
            column: "FeeDefinitionId");

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceLines_InvoiceId",
            table: "InvoiceLines",
            column: "InvoiceId");

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceLines_PurchaseKey",
            table: "InvoiceLines",
            column: "PurchaseKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceLines_RecurringItemId",
            table: "InvoiceLines",
            column: "RecurringItemId");

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceLines_ServiceDefinitionId",
            table: "InvoiceLines",
            column: "ServiceDefinitionId");

        migrationBuilder.CreateIndex(
            name: "IX_InvoiceLines_ServiceEnrollmentId",
            table: "InvoiceLines",
            column: "ServiceEnrollmentId");

        migrationBuilder.CreateIndex(
            name: "IX_Invoices_BillingAccountId",
            table: "Invoices",
            column: "BillingAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_Invoices_BillingGroupId",
            table: "Invoices",
            column: "BillingGroupId",
            unique: true,
            filter: "\"ClosedAt\" IS NULL AND \"BillingGroupId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Invoices_OneOffReference",
            table: "Invoices",
            column: "OneOffReference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_LineAdjustments_InvoiceLineId",
            table: "LineAdjustments",
            column: "InvoiceLineId");

        migrationBuilder.CreateIndex(
            name: "IX_LineAdjustments_OperationId",
            table: "LineAdjustments",
            column: "OperationId");

        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_BillingAccountId",
            table: "MoneyMovements",
            column: "BillingAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_DiscountId",
            table: "MoneyMovements",
            column: "DiscountId");

        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_InvoiceLineId",
            table: "MoneyMovements",
            column: "InvoiceLineId");

        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_OperationId",
            table: "MoneyMovements",
            column: "OperationId");

        migrationBuilder.CreateIndex(
            name: "IX_MoneyMovements_ReceiptId",
            table: "MoneyMovements",
            column: "ReceiptId");

        migrationBuilder.CreateIndex(
            name: "IX_Receipts_BillingAccountId",
            table: "Receipts",
            column: "BillingAccountId");

        migrationBuilder.CreateIndex(
            name: "IX_Receipts_Source_SourceAccount_ExternalId",
            table: "Receipts",
            columns: new[] { "Source", "SourceAccount", "ExternalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_RecurringItems_BillingGroupId",
            table: "RecurringItems",
            column: "BillingGroupId");

        migrationBuilder.CreateIndex(
            name: "IX_RecurringItems_FeeDefinitionId",
            table: "RecurringItems",
            column: "FeeDefinitionId");

        migrationBuilder.CreateIndex(
            name: "IX_RecurringItems_MembershipId",
            table: "RecurringItems",
            column: "MembershipId",
            unique: true,
            filter: "\"EndedAt\" IS NULL AND \"MembershipId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_RecurringItems_ServiceEnrollmentId",
            table: "RecurringItems",
            column: "ServiceEnrollmentId",
            unique: true,
            filter: "\"EndedAt\" IS NULL AND \"ServiceEnrollmentId\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_ServiceChanges_OperationId",
            table: "ServiceChanges",
            column: "OperationId");

        migrationBuilder.CreateIndex(
            name: "IX_ServiceChanges_ServiceEnrollmentId",
            table: "ServiceChanges",
            column: "ServiceEnrollmentId");

        migrationBuilder.CreateIndex(
            name: "IX_ServiceDefinitions_Code",
            table: "ServiceDefinitions",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ServiceEnrollments_ServiceDefinitionId",
            table: "ServiceEnrollments",
            column: "ServiceDefinitionId");

        migrationBuilder.CreateIndex(
            name: "IX_ServiceEnrollments_UserId_ServiceDefinitionId",
            table: "ServiceEnrollments",
            columns: new[] { "UserId", "ServiceDefinitionId" },
            unique: true,
            filter: "\"EndedAt\" IS NULL");

        migrationBuilder.AddForeignKey(
            name: "FK_Deliveries_Invoices_InvoiceId",
            table: "Deliveries",
            column: "InvoiceId",
            principalTable: "Invoices",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_InitialMembershipRequests_Invoices_InvoiceId",
            table: "InitialMembershipRequests",
            column: "InvoiceId",
            principalTable: "Invoices",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Deliveries_Invoices_InvoiceId",
            table: "Deliveries");

        migrationBuilder.DropForeignKey(
            name: "FK_InitialMembershipRequests_Invoices_InvoiceId",
            table: "InitialMembershipRequests");

        migrationBuilder.DropTable(
            name: "BankSyncStates");

        migrationBuilder.DropTable(
            name: "Coverage");

        migrationBuilder.DropTable(
            name: "FeePrices");

        migrationBuilder.DropTable(
            name: "LineAdjustments");

        migrationBuilder.DropTable(
            name: "MoneyMovements");

        migrationBuilder.DropTable(
            name: "ServiceChanges");

        migrationBuilder.DropTable(
            name: "Discounts");

        migrationBuilder.DropTable(
            name: "InvoiceLines");

        migrationBuilder.DropTable(
            name: "Receipts");

        migrationBuilder.DropTable(
            name: "BillingOperations");

        migrationBuilder.DropTable(
            name: "Invoices");

        migrationBuilder.DropTable(
            name: "RecurringItems");

        migrationBuilder.DropTable(
            name: "BillingGroups");

        migrationBuilder.DropTable(
            name: "Fees");

        migrationBuilder.DropTable(
            name: "ServiceEnrollments");

        migrationBuilder.DropTable(
            name: "ServiceDefinitions");

        migrationBuilder.DropIndex(
            name: "IX_InitialMembershipRequests_InvoiceId",
            table: "InitialMembershipRequests");

        migrationBuilder.DropIndex(
            name: "IX_Deliveries_InvoiceId",
            table: "Deliveries");

        migrationBuilder.DropIndex(
            name: "IX_BillingAccounts_PurchaseDepositReference",
            table: "BillingAccounts");

        migrationBuilder.DropColumn(
            name: "InvoiceId",
            table: "InitialMembershipRequests");

        migrationBuilder.DropColumn(
            name: "InvoiceId",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "InvoiceRevision",
            table: "Deliveries");

        migrationBuilder.DropColumn(
            name: "ClaimEmail",
            table: "BillingAccounts");

        migrationBuilder.DropColumn(
            name: "ClaimName",
            table: "BillingAccounts");

        migrationBuilder.DropColumn(
            name: "EmailInvoices",
            table: "BillingAccounts");

        migrationBuilder.DropColumn(
            name: "PurchaseDepositReference",
            table: "BillingAccounts");
    }
}
