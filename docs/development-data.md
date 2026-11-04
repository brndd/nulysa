# Development seed data

`seed-development-data` fills a local development database with a fixed synthetic
dataset: an administrator, two separate role-test accounts, a couple of dozen
members with varied payment and service states, applicants for the approval list,
payment-review receipts, and two services.

It is **Development-only**, explicit, and one-shot. It is never run by `migrate`,
web/worker startup, or a deployment. Run it only after resetting the local
development database.

```sh
bash scripts/dev.sh seed-development-data
```

The command is idempotent for convenience: if the seed administrator already
exists, it prints the known account list and changes nothing. It does not try to
repair a partial seed. A fresh seed uses the same short payment references as new
members and invoices. To see them throughout an already seeded demo, follow the
reset instructions below and seed again.

## Logins

All accounts use the default password:

```text
P4ssword!
```

Override it with `DevelopmentSeed__Password` before running the command if
needed. These are local ASP.NET Core Identity accounts; they are not created in
Keycloak. The emails use the reserved `.test` namespace and are not real addresses.

| Email | Role | Seeded state |
| --- | --- | --- |
| `admin@seed.nulysa.test` | Administrator + Superuser | Paid active member; every workspace |
| `board@seed.nulysa.test` | Administrator | Paid active member; admin pages, not System Console |
| `sysop@seed.nulysa.test` | Superuser | Paid active member; System Console, not board admin pages |
| `active@seed.nulysa.test` | Member | Active, paid, invoice email enabled |
| `door@seed.nulysa.test` | Member | Active membership and paid Door access |
| `door-free@seed.nulysa.test` | Member | New member, Door initial-free days, membership invoice unpaid |
| `door-due@seed.nulysa.test` | Member | Membership paid; Door renewal unpaid in grace |
| `grace@seed.nulysa.test` | Member | Unpaid membership renewal within renewal grace |
| `inactive@seed.nulysa.test` | Member | Inactive after grace; billing paused |
| `old-inactive@seed.nulysa.test` | Member | Inactive for many months |
| `new@seed.nulysa.test` | Member | New initial invoice completely unpaid |
| `partial@seed.nulysa.test` | Member | Initial invoice partially funded; money held |
| `credit@seed.nulysa.test` | Member | Overpaid; billing credit remains |
| `deposit@seed.nulysa.test` | Member | Purchase balance and a transfer into billing credit |
| `honorary@seed.nulysa.test` | Member | 100% discount; covered without a payment |
| `discount@seed.nulysa.test` | Member | 50% discount applied before payment |
| `granted@seed.nulysa.test` | Member | Administrator whole-month coverage grant |
| `oneoff@seed.nulysa.test` | Member | Open one-off invoice |
| `oneoff-paid@seed.nulysa.test` | Member | Paid one-off invoice |
| `stopped@seed.nulysa.test` | Member | Door renewal stopped; remaining Door coverage |
| `removed@seed.nulysa.test` | Member | Door removed immediately; prior coverage retained |
| `pending@seed.nulysa.test` | Member | Pending deletion by an administrator; login blocked |
| `resigned@seed.nulysa.test` | Member | Self-resigned; pending deletion; login blocked |
| `restored@seed.nulysa.test` | Member | Pending deletion cancelled by an administrator; expired coverage was not restored |
| `resumed@seed.nulysa.test` | Member | Previously inactive member resumed with a return period |
| `quarterly@seed.nulysa.test` | Member | Three-month billing interval |
| `email-off@seed.nulysa.test` | Member | Invoice-email preference disabled |
| `refunded@seed.nulysa.test` | Member | A completed refund recorded against credit |
| `trailer@seed.nulysa.test` | Member | Self-subscribed Trailer access |

The seed command prints the same list with the allocated user and account IDs.

## Applicants

These are application records, not login accounts. They have no password.

| Email | State |
| --- | --- |
| `applicant.verified@seed.nulysa.test` | Verified; visible in the approval list |
| `applicant.under18@seed.nulysa.test` | Verified under-18 applicant; highlighted in the approval list |
| `applicant.unverified@seed.nulysa.test` | Unverified; absent from the approval list |

The verification message for the unverified applicant is in the normal development
mail spool/console. The two verified applications can be approved immediately.

## Payment review and services

The seed creates these unassigned bank-review items:

| Reference | Amount | State |
| --- | --- | --- |
| `unknown-seed-reference` | 12.50 EUR | Review |
| `ignored-seed-reference` | 8.00 EUR | Ignored |
| `outgoing-seed-reference` | -20.00 EUR | Review; outgoing |

The two services are:

| Code | Name | Price | Free days | Grace days | Self-subscription |
| --- | --- | --- | --- | --- | --- |
| `door` | Door access | 10.00 EUR/month | 14 | 7 | No |
| `trailer` | Trailer access | 5.00 EUR/month | 0 | 7 | Yes |

`Door access` matches the association's admin-managed 24/7-access direction.
`Trailer access` is the synthetic service for exercising member self-subscription.
Neither service is seeded by an ordinary deployment or migration.

The `deposit@…` fixture creates purchase balance only when
`Billing:PurchaseBalanceEnabled=true`. `scripts/dev.sh` enables it by default;
without it, that account is still created as a normally paid member.

The seed command runs the normal billing reconciliation at the end and fails if
the journal, coverage and allocations do not reconcile.

## Reset

The command expects a fresh development database and does not delete existing rows.
To start over:

```sh
# Stop any running web/worker processes first.
docker compose -f compose.yaml down -v
bash scripts/dev.sh seed-development-data
```

`down -v` removes the local PostgreSQL volume. The local Keycloak setup is separate
and is not touched; these seed accounts are not Keycloak users.

The seed is not used by the automated test suite. Tests keep their own small,
controlled fixtures.
