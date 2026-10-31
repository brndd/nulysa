# S1 membership and essential mail

S1 implements application → email verification → board approval → password setup,
own/admin profile maintenance, verified email changes, password reset, resignation,
administrator termination/restoration, and actual scheduled erasure. Pages use
static SSR forms; password/cookie operations remain HTTP Razor Pages.

## Try it locally

Run `bash scripts/dev.sh`. Use synthetic data only. `/apply` is public; applicants
are not Identity users and cannot log in. In Development, the worker prints every
message to the console and also writes private JSON under
`.local/data-protection/mail/` (or `Mail__SpoolPath`). Open the link in either place
and confirm it; GET alone does not verify an address. There is no public mailbox
endpoint. These files contain secrets/personal data: do not serve, commit, or copy
them into logs; discard the synthetic mailbox after testing. The launcher forces
loopback links and the local spool, but preserves an explicit `Mail__Transport`
override so development can also send through SMTP.

Create a development operator as described in the README. The command provisions
a membership/billing owner, grants both `Administrator` and `Superuser`, and prints
the new user ID after commit. No separate board-role grant is needed.

For an existing account, the separate operator command remains available:

```sh
bash scripts/dev.sh grant-administrator 123
```

`revoke-administrator 123` removes it. These board-role commands do not grant
`Superuser`, create members or bypass admission.

Board workspaces are `/admin/applications` and `/admin/members`. They show only
verified, undecided applications, flag under-18 applicants without an age cutoff,
search members, edit details, show ID-only approval/lifecycle audit, and expose
pending deletions/mail failures. Members have `/profile` and `/resign`, not a directory.
Board and superuser policies both check live assignments, not old cookie roles.

## Billing handover — completed in S2

Approval atomically creates Identity/member/approval records, an association-local
joining anchor, a stable `BillingAccount`, an `InitialMembershipRequest`, and queued
welcome mail. [S2](billing.md) now consumes that handover in the same transaction,
issues the real initial invoice and queues its PDF. Standing uses actual coverage
and charge-specific grace. Deletion closes unfinished charges/releases holds while
preserving unspent claims. Existing S1 requests are consumed by due work after the
upgrade; configure a price effective on their original anchor before starting it.
Cancelled requests are not resurrected. Restoration does not invent grace/coverage.

## Mail configuration and failure handling

Production requires `Mail__Transport=Smtp`, an HTTPS `Mail__PublicUrl` ending in
`/`, `Mail__Host`, `Mail__Port` (usually 587), and `Mail__FromEmail` from a verified
sender domain. Set `Mail__Username` and `Mail__Password` when the relay requires
authentication; `Mail__EnableSsl` defaults to true for STARTTLS. Scaleway TEM and
other transactional providers can be used through their SMTP endpoints. Development
transport is forbidden in Production. Supply the association terms/privacy URL as
`Association__TermsUrl`; absent terms are explicitly development-only, not invented
legal terms. Configuring an adapter is not provider onboarding or permission to
deploy.

Essential templates are trusted plain text in English/Finnish, with explicit
`Membership__DefaultLanguage` fallback (`en` by default). They are mandatory operational
messages; newsletter preferences cannot suppress them. Editable/rich templates,
authoring and newsletter delivery remain S3 work. Identity password/change-email
tokens expire after one day; forgotten initial setup can use ordinary reset.

Every recipient has a `Delivery` record; Quartz contains only its random ID and
enqueue time. Scheduling shares the domain EF transaction. The worker polls the
persistent job store every `Worker:IdleWaitSeconds` (1 in Development, 5 in
Production), which bounds delivery latency. A persisted provider-wide
one-minute-window limit (`Mail__MaxPerMinute`, default 60), counting every attempt,
coordinates workers independently of delivery retention/erasure. SMTP delivery is
bounded to 15 seconds. Only transient 4xx replies retry (three total attempts);
permanent 5xx failures are terminal, while timeouts, lost connections and malformed
responses require review. An interrupted `Sending` record becomes `Review`, never a
blind resend. The due-work scan recovers pending intents with no remaining Quartz job.
Provider IDs/correlation headers assist investigation; acceptance is not inbox delivery.

Check the board attention list and superuser console; external monitoring must alert
on `mail_requires_review`, `aged_mail`, failed/stale schedules and aged work. Reconcile
uncertain sends with the mail provider before any deliberately designed repair; there is no arbitrary
retry/SQL editor in the portal. Provider failures are not retried merely by restarting.

Rejection immediately removes approval eligibility, queues its explanation, and erases
the application **in the provider-acceptance transaction**, not when enqueued. Failed/
uncertain rejection sends are escalated in those diagnostics. Their applicant payload
is erased after `Membership__RejectionRetentionDays` (default 7), even if undelivered;
only delivery ID/type/status/times remain, not a shadow applicant profile. Terminal
delivery metadata is pruned after 30 days. Operators must review failures promptly,
not retain rejected personal data indefinitely while attempting delivery.

## Dates, cleanup and erasure

- Unverified application retention: `Membership__UnverifiedRetentionHours`, default 24.
  Verification/resends cannot extend it; resend requests are spaced one minute apart.
- The minute-level `membership-due-work` job expires unverified/rejected payloads,
  clears expired pending email addresses, and diagnoses interrupted sends in bounded batches.
- Deletion waiting time: `Membership__DeletionWaitingDays`, default 14 calendar days
  in the association timezone, preserving the marking wall time (not rounded down
  to midnight). Eligibility is snapshotted when deletion begins; ambiguous times
  choose the later instant, and missing wall times wait until valid.
  The warning describes eligibility plus the 14-day batch cadence, not an exact promise.
- `final-deletion` first runs at the next association-local midnight, then every 14
  calendar days across DST. Installation preserves its persisted phase/next-fire time;
  missed runs coalesce. Normal waiting is 14–28 days; failures can delay, never accelerate.
- Begin-deletion blocks existing cookies and upstream OIDC use immediately, rotates
  the security stamp, revokes token/authorization chains, cancels unnecessary pending
  mail/billing requests and queues one warning. It does not revoke existing downstream
  Keycloak/application sessions. A send already in flight cannot be recalled.
- Administrator restoration is allowed until actual erasure, even after eligibility.
  It queues a confirmation to the member and does not restore expired coverage/credentials
  or repair pending-deletion payments.
- Final erasure rechecks under billing-account/member locks, deletes Identity/profile,
  member and authentication records and unnecessary deliveries, and detaches the
  financial owner. ID-only approval/lifecycle facts and the cancelled financial request
  remain; rejoining creates new IDs and never links by email. No completion mail is sent.

Before production, validate retention/lawful bases for historical IDs and audit reasons,
and extend the erasure runbook to the mail provider, Keycloak, exports/logs, encrypted backups and
restore-time deletion replay. Local erasure does not purge external mailboxes/backups.
Password-guessing protection, production provider/translation validation and the
later delivery/cutover gates remain mandatory.

## Evidence

Validated in Release on PostgreSQL 18.4: **56/56 standard tests** (about 30 seconds)
and **74/74 full tests** (about 140 seconds), with no skips. The branded Chromium
journey was rechecked after the final title changes. Release build, formatting,
migration-model consistency, development-launcher tests and the production-mode
container smoke also passed. No live SMTP account/delivery was exercised.

`MembershipTests`, `MembershipFormTests`, `MailTests` and `MailTransportTests` cover
PostgreSQL admission/rollback/uniqueness, verification expiry/resends, privacy/live
authorization, ID-only audit, rejection acceptance/failure retention, password setup/
reset before first login, restoration notification, account/token gates, restoration/
erasure races, local-calendar eligibility, fresh admission and provider classification/
rate limiting. Full-only tests execute real queued mail in
a worker process and the full Chromium form journey. `KeycloakTests` now uses real
S1 admission, password setup, verified email change and termination commands.
