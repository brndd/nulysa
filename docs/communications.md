# S3 communications backend

Communications is a set of application services for the server-side UI. It uses
the existing SMTP transport, worker and PostgreSQL Quartz store. Resolve these
services in a fresh scope per operation in a long-lived editor circuit. Apply the
EF migrations before starting web and worker.

- `Campaigns` lists, saves and copies newsletters and official notices. A
  draft's revision guards edits. Copy a previous campaign to reuse its content.
  The admin draft and template editors use Markdown with Write and Preview tabs.
  The standard formatting controls use GitHub's MIT-licensed
  [Markdown toolbar](https://github.com/github/markdown-toolbar-element).
  The project vendors the unmodified `dist/index.js` and `LICENSE` from
  `@github/markdown-toolbar-element@2.1.0` under `wwwroot/vendor`.
  Markdig converts supported syntax into the existing message blocks for both
  preview and save. The saved variant keeps the source for editing. The mail
  renderer generates the HTML preview and both outgoing formats from validated
  blocks. Raw HTML, images and unsupported Markdown constructs are rejected.
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
  delay only the affected delivery. Bulk campaign batches are spaced at the
  configured send rate so later operational jobs can run before future bulk jobs.
  Operational mail has higher Quartz priority at the same fire time. The shared
  rate limit still applies to every delivery.
- `Progress` reports recipient states and attempts, with test sends identified
  separately. `RetryFailed` accepts selected definite failures, confirmation
  and a reason. It records who retried them and creates a new job generation so
  a stale job cannot dispatch the retry. Accepted, cancelled and uncertain
  deliveries are excluded. Recipient records follow existing erasure and
  terminal delivery retention rules (30 days); progress exposes how many are
  no longer retained. Campaign outcomes appear in progress. Campaign delivery
  failures and backlog age are excluded from the operational mail alarms. The
  System Console shows the oldest campaign and operational jobs separately.
- `OperationalTemplates` manages one subject and Markdown body per message
  kind and language. Shipped defaults are embedded from
  `src/Nulysa/Communications/Templates/*.md`, with a `Subject:` header followed
  by a blank line and the body. Application startup validates the shipped files.
  The worker supplies named fields from the per-kind contract. It resolves the
  recipient's language first, using the configured fallback language if there is
  no shipped translation. A valid override takes precedence over the shipped
  version for the selected language. An invalid override stays available for
  editing and the admin overview links to it while mail uses the shipped version.
  Existing wrappers open as expanded Markdown and saving stores the expanded source.
  Invoice PDFs and delivery eligibility continue to be handled by the worker.
- `NewsletterPreferences` reads and changes the signed-in member's own
  preference. Newsletter email includes an opaque, 90-day unsubscribe token
  tied to the address used at dispatch. Anonymous
  `POST /communications/newsletter/unsubscribe?token=...` applies the opt-out;
  a GET does not change it. Add the browser confirmation screen at that path
  during the UI pass. SMTP includes the one-click headers for HTTPS links; for
  mail clients to offer the action, configure the provider to DKIM-sign both
  headers as described in [RFC 8058](https://www.rfc-editor.org/rfc/rfc8058.html#section-4).

Open **Review and send saved draft** after saving a campaign to preview the
audience, choose fallbacks and queue a test email. Confirm the reviewed audience
to queue the bulk send. Queued campaigns move to **Campaign history**, where
administrators can view the frozen content, copy it into a new draft, and retry
definite failures with a reason. A campaign is complete when no deliveries are
queued or sending. Its sent count records provider acceptance and survives retention or
erasure of individual delivery records.
Member preferences and browser unsubscribe confirmation are separate UI work.

Automatic templates use Fluid to parse and render a subset of Liquid. Fields use
`{{field_name}}` in text and
`[label]({{action_url}})` or `<{{action_url}}>` for a generated link. The editor
shows which fields are required in the body. Values are inserted after Markdown
parsing, so their contents are treated as plain text or as one validated URL.
Wrap literal braces in `{% raw %}{{example}}{% endraw %}` in either the
subject or body. Optional sections use `{% if field_name %}`, an optional
`{% else %}`, and `{% endif %}`. Required fields must appear in every
rendered branch. Liquid expressions can reference the named fields directly.
Existing templates written in the earlier syntax are converted on read and
stored as Liquid when saved. The editor previews with sample values. When changing
a field contract, keep compatible names where possible. Removing a name or adding
a required field can invalidate a stored override. Wording-only updates take
effect automatically in languages using the shipped template.
