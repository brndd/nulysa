using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nulysa.Data.Migrations;

/// <inheritdoc />
public partial class Initial : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
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
            name: "AspNetRoles",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoles", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUsers",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Municipality = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                DateOfBirth = table.Column<DateOnly>(type: "date", nullable: true),
                Language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "en"),
                Nickname = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                MatrixId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                PendingEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                PendingEmailVersion = table.Column<Guid>(type: "uuid", nullable: true),
                PendingEmailRequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                OidcSubject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Admitted = table.Column<bool>(type: "boolean", nullable: false),
                PendingDeletionAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                PasswordHash = table.Column<string>(type: "text", nullable: true),
                SecurityStamp = table.Column<string>(type: "text", nullable: true),
                ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                PhoneNumber = table.Column<string>(type: "text", nullable: true),
                PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUsers", x => x.Id);
            });

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
            name: "Observations",
            columns: table => new
            {
                Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                LastSeen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Observations", x => x.Name);
            });

        migrationBuilder.CreateTable(
            name: "OpenIddictApplications",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                ApplicationType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                ClientId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                ClientSecret = table.Column<string>(type: "text", nullable: true),
                ClientType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                ConsentType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                DisplayName = table.Column<string>(type: "text", nullable: true),
                DisplayNames = table.Column<string>(type: "text", nullable: true),
                JsonWebKeySet = table.Column<string>(type: "text", nullable: true),
                Permissions = table.Column<string>(type: "text", nullable: true),
                PostLogoutRedirectUris = table.Column<string>(type: "text", nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                RedirectUris = table.Column<string>(type: "text", nullable: true),
                Requirements = table.Column<string>(type: "text", nullable: true),
                Settings = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictApplications", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OpenIddictScopes",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                Description = table.Column<string>(type: "text", nullable: true),
                Descriptions = table.Column<string>(type: "text", nullable: true),
                DisplayName = table.Column<string>(type: "text", nullable: true),
                DisplayNames = table.Column<string>(type: "text", nullable: true),
                Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                Resources = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictScopes", x => x.Id);
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
            name: "AspNetRoleClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RoleId = table.Column<long>(type: "bigint", nullable: false),
                ClaimType = table.Column<string>(type: "text", nullable: true),
                ClaimValue = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserClaims",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<long>(type: "bigint", nullable: false),
                ClaimType = table.Column<string>(type: "text", nullable: true),
                ClaimValue = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                table.ForeignKey(
                    name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserLogins",
            columns: table => new
            {
                LoginProvider = table.Column<string>(type: "text", nullable: false),
                ProviderKey = table.Column<string>(type: "text", nullable: false),
                ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                UserId = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                table.ForeignKey(
                    name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserRoles",
            columns: table => new
            {
                UserId = table.Column<long>(type: "bigint", nullable: false),
                RoleId = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                    column: x => x.RoleId,
                    principalTable: "AspNetRoles",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "AspNetUserTokens",
            columns: table => new
            {
                UserId = table.Column<long>(type: "bigint", nullable: false),
                LoginProvider = table.Column<string>(type: "text", nullable: false),
                Name = table.Column<string>(type: "text", nullable: false),
                Value = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                table.ForeignKey(
                    name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                    column: x => x.UserId,
                    principalTable: "AspNetUsers",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "BillingAccounts",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                UserId = table.Column<long>(type: "bigint", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                PurchaseDepositReference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                EmailInvoices = table.Column<bool>(type: "boolean", nullable: false),
                ClaimName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ClaimEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
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
            name: "OpenIddictAuthorizations",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                ApplicationId = table.Column<string>(type: "text", nullable: true),
                ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                Scopes = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictAuthorizations", x => x.Id);
                table.ForeignKey(
                    name: "FK_OpenIddictAuthorizations_OpenIddictApplications_Application~",
                    column: x => x.ApplicationId,
                    principalTable: "OpenIddictApplications",
                    principalColumn: "Id");
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
            name: "OpenIddictTokens",
            columns: table => new
            {
                Id = table.Column<string>(type: "text", nullable: false),
                ApplicationId = table.Column<string>(type: "text", nullable: true),
                AuthorizationId = table.Column<string>(type: "text", nullable: true),
                ConcurrencyToken = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                CreationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ExpirationDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Payload = table.Column<string>(type: "text", nullable: true),
                Properties = table.Column<string>(type: "text", nullable: true),
                RedemptionDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ReferenceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                Subject = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                Type = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OpenIddictTokens", x => x.Id);
                table.ForeignKey(
                    name: "FK_OpenIddictTokens_OpenIddictApplications_ApplicationId",
                    column: x => x.ApplicationId,
                    principalTable: "OpenIddictApplications",
                    principalColumn: "Id");
                table.ForeignKey(
                    name: "FK_OpenIddictTokens_OpenIddictAuthorizations_AuthorizationId",
                    column: x => x.AuthorizationId,
                    principalTable: "OpenIddictAuthorizations",
                    principalColumn: "Id");
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
                ProviderId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                InvoiceId = table.Column<long>(type: "bigint", nullable: true),
                InvoiceRevision = table.Column<int>(type: "integer", nullable: true)
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
                table.ForeignKey(
                    name: "FK_Deliveries_Invoices_InvoiceId",
                    column: x => x.InvoiceId,
                    principalTable: "Invoices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "InitialMembershipRequests",
            columns: table => new
            {
                BillingAccountId = table.Column<long>(type: "bigint", nullable: false),
                Anchor = table.Column<DateOnly>(type: "date", nullable: false),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                InvoiceId = table.Column<long>(type: "bigint", nullable: true)
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
                table.ForeignKey(
                    name: "FK_InitialMembershipRequests_Invoices_InvoiceId",
                    column: x => x.InvoiceId,
                    principalTable: "Invoices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
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
                DiscountId = table.Column<long>(type: "bigint", nullable: true),
                ReversesMovementId = table.Column<long>(type: "bigint", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_MoneyMovements", x => x.Id);
                table.CheckConstraint("CK_Movement_Amount", "\"Amount\" > 0");
                table.CheckConstraint("CK_Movement_Discount", "(\"Kind\" = 'PurchaseCredit') = (\"DiscountId\" IS NOT NULL)");
                table.CheckConstraint("CK_Movement_Line", "(\"Kind\" IN ('ReceiptToLine','BillingToLine','LineToBilling','LinePurchase','PurchaseCredit')) = (\"InvoiceLineId\" IS NOT NULL)");
                table.CheckConstraint("CK_Movement_Receipt", "(\"Kind\" IN ('ReceiptToLine','ReceiptToBilling','ReceiptToPurchase','BillingReceiptReversal','PurchaseReceiptReversal')) = (\"ReceiptId\" IS NOT NULL)");
                table.CheckConstraint("CK_Movement_Reversal", "(\"Kind\" IN ('BillingReceiptReversal','PurchaseReceiptReversal')) = (\"ReversesMovementId\" IS NOT NULL)");
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
                    name: "FK_MoneyMovements_MoneyMovements_ReversesMovementId",
                    column: x => x.ReversesMovementId,
                    principalTable: "MoneyMovements",
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
            name: "IX_AspNetRoleClaims_RoleId",
            table: "AspNetRoleClaims",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "RoleNameIndex",
            table: "AspNetRoles",
            column: "NormalizedName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserClaims_UserId",
            table: "AspNetUserClaims",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserLogins_UserId",
            table: "AspNetUserLogins",
            column: "UserId");

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUserRoles_RoleId",
            table: "AspNetUserRoles",
            column: "RoleId");

        migrationBuilder.CreateIndex(
            name: "EmailIndex",
            table: "AspNetUsers",
            column: "NormalizedEmail",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AspNetUsers_OidcSubject",
            table: "AspNetUsers",
            column: "OidcSubject",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "UserNameIndex",
            table: "AspNetUsers",
            column: "NormalizedUserName",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BillingAccounts_PurchaseDepositReference",
            table: "BillingAccounts",
            column: "PurchaseDepositReference",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_BillingAccounts_UserId",
            table: "BillingAccounts",
            column: "UserId",
            unique: true,
            filter: "\"UserId\" IS NOT NULL");

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
            name: "IX_CreditLots_BillingAccountId_ReceivedOn",
            table: "CreditLots",
            columns: new[] { "BillingAccountId", "ReceivedOn" });

        migrationBuilder.CreateIndex(
            name: "IX_CreditLots_ReceiptId",
            table: "CreditLots",
            column: "ReceiptId");

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_ApplicationId",
            table: "Deliveries",
            column: "ApplicationId");

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_InvoiceId",
            table: "Deliveries",
            column: "InvoiceId");

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_State_CreatedAt",
            table: "Deliveries",
            columns: new[] { "State", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_UserId",
            table: "Deliveries",
            column: "UserId");

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
            name: "IX_InitialMembershipRequests_InvoiceId",
            table: "InitialMembershipRequests",
            column: "InvoiceId");

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
            name: "IX_LifecycleActions_UserId",
            table: "LifecycleActions",
            column: "UserId");

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
            name: "IX_MoneyMovements_EffectiveOn",
            table: "MoneyMovements",
            column: "EffectiveOn");

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
            name: "IX_MoneyMovements_ReversesMovementId",
            table: "MoneyMovements",
            column: "ReversesMovementId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictApplications_ClientId",
            table: "OpenIddictApplications",
            column: "ClientId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictAuthorizations_ApplicationId_Status_Subject_Type",
            table: "OpenIddictAuthorizations",
            columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictScopes_Name",
            table: "OpenIddictScopes",
            column: "Name",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictTokens_ApplicationId_Status_Subject_Type",
            table: "OpenIddictTokens",
            columns: new[] { "ApplicationId", "Status", "Subject", "Type" });

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictTokens_AuthorizationId",
            table: "OpenIddictTokens",
            column: "AuthorizationId");

        migrationBuilder.CreateIndex(
            name: "IX_OpenIddictTokens_ReferenceId",
            table: "OpenIddictTokens",
            column: "ReferenceId",
            unique: true);

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

        // EF cannot model triggers; keep this when regenerating the baseline.
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION nulysa_username_immutable() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."UserName" IS DISTINCT FROM OLD."UserName"
                   OR NEW."NormalizedUserName" IS DISTINCT FROM OLD."NormalizedUserName" THEN
                    RAISE EXCEPTION 'UserName is immutable';
                END IF;
                RETURN NEW;
            END
            $$;

            CREATE TRIGGER "AspNetUsers_UserName_Immutable"
                BEFORE UPDATE ON "AspNetUsers"
                FOR EACH ROW EXECUTE FUNCTION nulysa_username_immutable();
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""DROP TRIGGER IF EXISTS "AspNetUsers_UserName_Immutable" ON "AspNetUsers"; DROP FUNCTION IF EXISTS nulysa_username_immutable();""");

        migrationBuilder.DropTable(
            name: "Approvals");

        migrationBuilder.DropTable(
            name: "AspNetRoleClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserClaims");

        migrationBuilder.DropTable(
            name: "AspNetUserLogins");

        migrationBuilder.DropTable(
            name: "AspNetUserRoles");

        migrationBuilder.DropTable(
            name: "AspNetUserTokens");

        migrationBuilder.DropTable(
            name: "BankSyncStates");

        migrationBuilder.DropTable(
            name: "Coverage");

        migrationBuilder.DropTable(
            name: "CreditLots");

        migrationBuilder.DropTable(
            name: "Deliveries");

        migrationBuilder.DropTable(
            name: "FailureObservations");

        migrationBuilder.DropTable(
            name: "FeePrices");

        migrationBuilder.DropTable(
            name: "InitialMembershipRequests");

        migrationBuilder.DropTable(
            name: "LifecycleActions");

        migrationBuilder.DropTable(
            name: "LineAdjustments");

        migrationBuilder.DropTable(
            name: "MailRateWindows");

        migrationBuilder.DropTable(
            name: "MoneyMovements");

        migrationBuilder.DropTable(
            name: "Observations");

        migrationBuilder.DropTable(
            name: "OpenIddictScopes");

        migrationBuilder.DropTable(
            name: "OpenIddictTokens");

        migrationBuilder.DropTable(
            name: "ServiceChanges");

        migrationBuilder.DropTable(
            name: "AspNetRoles");

        migrationBuilder.DropTable(
            name: "Applications");

        migrationBuilder.DropTable(
            name: "Discounts");

        migrationBuilder.DropTable(
            name: "InvoiceLines");

        migrationBuilder.DropTable(
            name: "Receipts");

        migrationBuilder.DropTable(
            name: "OpenIddictAuthorizations");

        migrationBuilder.DropTable(
            name: "BillingOperations");

        migrationBuilder.DropTable(
            name: "Invoices");

        migrationBuilder.DropTable(
            name: "RecurringItems");

        migrationBuilder.DropTable(
            name: "OpenIddictApplications");

        migrationBuilder.DropTable(
            name: "BillingGroups");

        migrationBuilder.DropTable(
            name: "Fees");

        migrationBuilder.DropTable(
            name: "Members");

        migrationBuilder.DropTable(
            name: "ServiceEnrollments");

        migrationBuilder.DropTable(
            name: "BillingAccounts");

        migrationBuilder.DropTable(
            name: "ServiceDefinitions");

        migrationBuilder.DropTable(
            name: "AspNetUsers");
    }
}
