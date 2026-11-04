# S3 communications backend

Communications is a set of application services for the server-side UI. It uses
the existing SMTP transport, worker and PostgreSQL Quartz store. Resolve these
services in a fresh scope per operation in a long-lived editor circuit. Apply the
EF migrations before starting web and worker.

- `Campaigns` lists, saves and copies newsletters and official notices. A
  draft's revision guards edits. Copy a previous campaign to reuse its content.
  Content is a JSON document of headings, paragraphs, bullet items and quotes
  with optional bold, italic and absolute links. Use this structure in the
  editor adapter. The server validates it and generates both an HTML fragment
  and a plain-text body; no raw HTML, images or scripts are accepted.
- `Preview` groups current verified members by preferred language, including
  inactive members. Each missing language needs an explicit choice of a saved
  variant. The preview returns the outgoing subject and bodies for each group
  and, when the audience is nonempty and all variants resolve, a confirmation
  token. The UI should present the result and require an affirmative action
  before calling `Send` with that token and the same fallback choices. Tokens
  are valid for 20 minutes, for the same administrator, draft revision and
  recipient selection. A stale selection requires a fresh preview.
- `SendTest` queues a snapshot of the selected saved variant to one address.
  The test is marked in the subject and contains no live unsubscribe link.
  The encrypted snapshot expires after one day and is cleared when delivery
  reaches a terminal state.
- `Send` freezes the draft and writes its per-recipient records and Quartz
  jobs in one transaction. Dispatch uses the current verified address and
  rechecks membership and pending deletion. It rechecks newsletter opt-outs
  separately; official notices ignore newsletter and invoice preferences.
  Language content stays as approved in the preview. SMTP sends both MIME
  alternatives. A temporary SMTP rejection records the next attempt time before
  scheduling the retry. Due-work maintenance can replace missing jobs. SMTP 421
  pauses new sends across workers for one minute. Other temporary SMTP rejections
  delay only the affected delivery. Operational mail has higher Quartz trigger
  priority than bulk mail when due together.
- `Progress` reports recipient states and attempts, with test sends identified
  separately. `RetryFailed` accepts selected definite failures, confirmation
  and a reason. It records who retried them and creates a new job generation so
  a stale job cannot dispatch the retry. Accepted, cancelled and uncertain
  deliveries are excluded. Recipient records follow existing erasure and
  terminal delivery retention rules (30 days); progress exposes how many are
  no longer retained. Campaign outcomes appear in progress. Campaign delivery
  failures and backlog age are excluded from the operational mail alarms.
- `OperationalTemplates` lists, saves and resets automatic mail overrides.
  Every saved override requires a fallback variant. The subject can use
  `{{subject}}` and `{{association}}`. The required operational details block
  includes the existing translated notice, account links and instructions;
  invoice PDFs and all delivery eligibility rules stay in the worker. The
  default policy uses the recipient's built-in translation or the configured
  `Membership:DefaultLanguage`. When adding a language, add its operational
  translations or use the visible fallback. Override language variants require
  a translation for the operational details block.
- `NewsletterPreferences` reads and changes the signed-in member's own
  preference. Newsletter email includes an opaque, 90-day unsubscribe token
  tied to the address used at dispatch. Anonymous
  `POST /communications/newsletter/unsubscribe?token=...` applies the opt-out;
  a GET does not change it. Add the browser confirmation screen at that path
  during the UI pass. SMTP includes the one-click headers for HTTPS links; for
  mail clients to offer the action, configure the provider to DKIM-sign both
  headers as described in [RFC 8058](https://www.rfc-editor.org/rfc/rfc8058.html#section-4).

Draft authoring, preview and progress screens, and the member preference and
browser unsubscribe screens are left for the UI pass.
