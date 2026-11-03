# Member portal and administration UI design

**Agreed design direction; not an implementation-completion claim.** The member
portal, separate administration workspace, responsiveness, dark mode and selective
interactivity described here were reviewed together. Association theming, a Default
theme and a Tampere Hacklab theme are also agreed requirements. Detailed routes,
component/configuration names, colours and breakpoints remain engineering proposals.

[SPEC.md](SPEC.md) remains authoritative for domain behaviour and permissions.
[ARCHITECTURE.md](ARCHITECTURE.md) owns rendering, authentication, commands and
transaction boundaries. This document records UI direction and acceptance criteria;
it does not introduce new membership, payment or storage policies. S0–S2 provide
working membership and billing features; the shared shell and member-facing tasks
are now styled and organised. The board dashboard and registry/member workspace
and contextual financial workflows are implemented. Later feature descriptions constrain their
delivery slices; they are not instructions to implement future features now.

The first UI foundation increment now implements the shared responsive shell,
two light/dark themes, browser appearance preferences and a local stylesheet hook.
See [implementation scope and configuration](../docs/ui.md). The member slice now
includes a general front page, account overview and separate billing details,
highlighted attention, compact bills/readable history, profile and
invoice-email settings, action confirmations and success feedback. **UI1's shared
foundation and UI2's member experience are delivered for the current feature set.**
UI3 board administration is delivered for the current feature set: the attention
dashboard, registry/member workspace, inline application review and contextual
financial workflows are implemented. UI4 now adds Finnish/English resources and language
selection, culture-aware forms, bounded member financial reads and expanded automated
accessibility/responsive evidence. Supplied branding and manual handoff checks remain. Section 13 is the current UI work docket; later
feature descriptions elsewhere are not unfinished UI2 requirements.

## 1. Goals and boundaries

Build a calm, task-oriented association portal: **my hackerspace account**, not a
billing application with additional modules attached. Reduce the work required for
members to understand their standing and for volunteers to administer it.

- Make ordinary tasks obvious without exposing the underlying data model.
- Work on desktop and mobile, including occasional administration from a phone.
- Support light and dark mode equally, rather than patching in dark mode later.
- Allow association identity and trusted stylesheets without maintaining a fork.
- Accommodate dense administration and future interactive workspaces within one
  visual language.
- Preserve accessibility, privacy, server authorization and financial correctness
  regardless of theme or rendering mode.
- Keep the frontend maintainable by a small volunteer group.

Do not build a dashboard widget framework, theme marketplace/editor, plugin loader,
generic CRUD administration, separate frontend application or global interactive
circuit. Do not add speculative directory, calendar, shop or social features to
fill navigation. Space for future features is not a commitment to implement them.

## 2. Audiences and workspaces

| Audience | Experience and boundary |
| --- | --- |
| Applicant/signed-out visitor | Simple public shell for applying, email verification, terms and login/password flows. No pending-applicant portal. |
| Member | Own membership, services, payments, profile and permitted operational tools. No directory or other members' profiles. |
| Board administrator | Purpose-built registry, finance, communications and business-configuration workspace. One board role, not separate finance/membership roles. |
| Trainer — S4 | Narrow qualification lookup and grant/revoke workspace. No general registry, contact or financial access. |
| Superuser | Separately labelled and authorised System Console, not ordinary board administration. |

Board administrators can switch between **Member portal** and **Administration**.
This changes workspace/navigation, not identity or permissions: it is not
impersonation. Show the active workspace clearly. Administrative member pages
identify the subject by name and member number so “my account” and “this member”
cannot be confused.

Board and superuser authority remain independent. Protect routes, queries and
commands regardless of navigation visibility. Trainer tools use qualification-scoped
authorization. Neither workspace nor appearance selection grants authority.

## 3. Information architecture

### Member navigation

| Area | Contents |
| --- | --- |
| Overview | General front page at `/`: highlighted actions and membership standing today; future storage/access summaries arrive with those features. |
| Membership & payments: Account overview | Membership/service standing, balances, deposit/transfer tools, subscriptions and billing settings at `/services`. |
| Membership & payments: Billing details | Outstanding bills, payment instructions, bill history and transaction history at `/billing`. |
| Space access — S4 | Doors, access requirements and own registered fobs. |
| Machines & training — S4 | Machine availability, qualifications and unmet requirements. Distinct from doors despite shared infrastructure. |
| Storage — S5 | Rentals, group availability, queue entries, offers and checkout/renewal. |

The account section has one **Settings** entry at `/profile`, containing personal
details, communication preferences and account/privacy actions, plus **Log out**. **Theme** is a top-bar dropdown, with an adjacent sun/moon colour-mode switch and a compact flag language dropdown, outside account settings.
Sidebar destinations are grouped into sections, with matching page tabs for sibling
pages so navigation remains available when the mobile sidebar is closed. Account/membership settings provide resignation and
personal-data/privacy requests; resignation is not a homepage primary action.
If privacy requests are handled by contacting administrators, present that route
honestly rather than imply an automated workflow.

Do not show empty navigation advertising unimplemented features. When an implemented
area is restricted by membership standing, explain why and how to resolve it without
exposing protected content. Inactive members retain profile, payment, privacy and
resignation access, plus convenient membership/services-page access.

### Administration navigation

The sidebar uses visible groups with non-link headings. Only implemented destinations
appear; sibling page tabs mirror each group on its main pages.

| Section | Current destinations |
| --- | --- |
| Needs attention | Board landing page at `/admin`. |
| Members | Member registry; Application review. |
| Finances | Payment review; Manual import; Accounting and exports; Retained balances. |
| Communications | Mail problems. Drafts/templates/audiences arrive with S3. |
| Association settings | Fees and prices; Services. |

Access/training (S4) and Storage (S5) add their groups when implemented. The System
Console remains a separate authorised workspace. Single-page groups need no redundant
tab row. Record pages use breadcrumbs; member records replace area tabs with their
four member tabs. At most one row of page tabs appears on a page. In-page heading
shortcuts use a smaller “On this page” treatment, and task choices use ordinary links
or buttons rather than tab styling.

Use compact controls, lists and field groups on busy task pages; large overview
controls should not set the density of every page. Rarely adjusted review thresholds
belong inside their count card as secondary controls.

Use related subnavigation within areas rather than a flat list of every screen.
The System Console stays separately labelled, outside normal board task groupings;
its initial implementation remains read-only.

### Navigation behaviour

- Use real links and durable URLs for pages and section tabs. Filters, sorting and
  pagination belong in query parameters where practical.
- `/` is the general front page, separate from the account overview at `/services`.
  `/billing` holds outstanding bills and both histories. Account overview and Billing
  details share reciprocal page tabs.
  There are no separate `/balances` or `/billing/history` destinations.
- Every administration page below `/admin` has a breadcrumb beginning with
  Administration, followed by actual parent destinations and a plain current item.
  Do not add breadcrumbs automatically to shallow member-portal pages.
- One sidebar destination is selected. Nested records select their parent destination;
  exact pages use `aria-current="page"`, parents use `aria-current="location"`.
- Admin headers use breadcrumbs, one page heading, compact record identity, page tabs,
  then feedback and content. Record identity does not introduce a second page heading.
- Returning from a record preserves the originating search/filter where practical.
- No universal member search. Registry and trainer lookups have different data
  permissions and must not be conflated.

## 4. Shell, layout and responsive behaviour

Use a compact header, persistent desktop sidebar, main content region and small
footer. The header carries association identity, workspace context and the account
menu. Include a skip-to-content link and clear current-navigation state.

Association identity is more prominent than the product name. Page titles include
association identity where useful; retain unobtrusive “Powered by Nulysa” attribution.
Public/account pages use a simplified shell with the same branding, appearance and
form treatment, but no member/admin navigation.

On mobile, replace the sidebar with an accessible disclosure/drawer opened by a
hamburger button in the top bar, with an accessible “Menu” label. Do not depend on
hover, an icon-only rail, or a fixed bottom tab bar that
assumes the portal will always have only a few destinations. Opening navigation
must not require an Interactive Server connection.

| Content layout | Use |
| --- | --- |
| Reading/form | Application, profile, ordinary settings; bounded field width and comfortable line length. |
| Standard | Overview, membership/services, bill detail; one or two columns as space permits. |
| Wide | Registry, payment review and accounting. |
| Workspace | Storage map and justified spatial/editor interactions. |

Use two primary responsive modes: desktop with the sidebar at 768 CSS pixels and
above, and mobile with its disclosure menu below 768. Balance cards use two columns
in desktop mode and one in mobile mode, without an auto-fit transition before the
sidebar breakpoint. Other field groups may adapt within these modes; they must not
cause content to expand back into multiple columns when the sidebar disappears.

- Forms collapse to one column with labels above controls.
- Summaries and actions wrap in a meaningful reading order.
- Simple lists can become stacked summaries, preserving field labels/relationships.
- Dense accounting tables may scroll within a clearly labelled region; keep the
  page itself within the viewport and make additional columns discoverable.
- Supporting panels move below content or onto detail pages on small screens.
- Do not remove essential actions on mobile or shrink text to fit desktop layouts.
- Fixed controls must not obscure fields, errors or the on-screen keyboard.

## 5. Member experience

### 5.1 Overview

Answer, in order: **Do I need to do anything? What is my standing? What can I use?
Where do I go next?**

1. An action-needed region, only when relevant: payment deadline, grace warning,
   storage offer or pending email verification.
2. Concise membership standing, including relevant validity/grace dates.
3. Applicable service/storage summaries as those features become available.
4. A small set of useful shortcuts.

Use specific actions such as **View bill and payment instructions** or **Review
storage offer**. Avoid duplicating the same notice across banners and cards. When
nothing needs attention, say so briefly rather than filling the screen with green
indicators, charts and meaningless totals.

Illustrative structure, not fixed copy or a requirement to build storage now:

```text
Overview

Action needed
Membership payment due …
[View bill and payment instructions]

Your membership                  Your services
Active                           24/7 access
Covered through …                Available through …

Your storage                     Quick links
Shelf B-12                       My details
Renewal available …              Register a fob
```

Future portal features can contribute concise summaries without a configurable
widget engine. Dashboard status is informational, never purchase/access authority.

### 5.2 Membership and services

Separate basic membership from optional subscriptions. Show standing, paid/granted
coverage, renewal intention, billing interval and permitted next actions.

- Distinguish paid coverage, genuine free grants and active grace; grace is not
  purchased time.
- Paid does not necessarily mean usable: inactive membership, missing qualification
  or resource unavailability can still block use. Explain the applicable reason.
- Show “Renewal stopped; available through …” rather than imply remaining time was
  immediately cancelled.
- Explain administrator-added services with a contact route. Do not invent a
  service application/approval workflow for non-self-subscribable services.
- Explain when interval changes take effect; do not imply a selection rewrites an
  issued bill or its reference/timing.
- Inactive members can request the agreed return-period workflow without settling
  expired requests or fictitious inactive-month arrears. Show administrator approval
  as the next step where configured.

### 5.3 Bills and payment instructions

Outstanding requests precede history; each bill has a detail view. Member-facing
wording can use ordinary **Bills**, while preserving advance-payment/no-debt policy.
Show active/upcoming discounts by fee and validity dates, linking outstanding bills
whose stored pricing includes the discount. Do not infer a repriced bill merely
from a current discount.

Keep distinct billing groups/references/schedules rather than flattening everything
into one payable total.

The account overview puts a brief highlighted **Needs your attention** panel first,
with outstanding-bill counts linking to Billing details and pending email warnings.
It then shows membership, service and balance status. Deposit guidance lives inside
the purchase-balance card; transfers use its adjacent dialog. **Billing settings**
groups invoice-email preferences and billing intervals. Administrator free-day grants,
removals and financial-adjustment links appear only in the administration view.

Billing details prioritises all outstanding requests, then a collapsed, newest-first
previous-bills list and a separate transaction-history list. Both histories default
to ten rows and retain independent URL pagination. Running balances are calculated
chronologically before reversing presentation order. Paging either history must not
hide current outstanding requests or change the other history's page. Current
balances and transfer/deposit tools live on the overview, not among historical totals.

Bill detail presents:

- Description, covered periods, status and the single public due date.
- Charges, billing credit applied, direct payments allocated and remaining amount.
- Item-level completion/partial funding where needed to explain standing.
- Bank recipient, IBAN, reference and amount associated with this specific bill.
- Copyable details and a current PDF. Details remain selectable/readable without
  clipboard JavaScript or downloading the document.
- Other payment methods only when implemented/available. MobilePay remains a
  required later integration, not a decorative disabled button.

A combined bill can remain open after membership is paid. Do not label the member
overdue because an optional service is unfinished. No per-line editable due-date
UI. Label forecasts as estimates, separate from issued payment instructions.

Distinguish initiated/unconfirmed payments from confirmed receipts. Explain bank
processing delay without promising immediate access or offering “mark paid.” Where
useful, show when payment information was updated. A provider's browser redirect
is not proof of payment.

### 5.4 Balances, deposits and transfers

| Display | Explanation |
| --- | --- |
| Billing credit | Automatically used towards valid recurring bills. |
| Purchase balance | For explicit purchases such as storage; not automatically swept into recurring bills. |
| Allocated to unfinished bills | Already committed funds, not another freely spendable balance. |

Never express unpaid requests as a negative wallet balance. Hide purchase/deposit
controls when disabled, preserving any financial history that still needs display.

Deposit instructions use the permanent purchase-deposit reference. Explain that
confirmation makes funds available but does not buy storage, accept an offer or
subscribe to a service.

Replace the current direction checkbox with explicit **From**/**To** choices,
available source amount and amount entry. Before confirmation, warn that moving
funds to billing credit can immediately commit/spend them on valid recurring bills,
so they may not be transferable back. The displayed amount does not lock funds;
the command revalidates availability.

History prioritises understandable description, effective date, amount and resulting
available balance. Put internal movement/lot IDs and processing timestamps in
supporting detail or administration. Keep useful authorised traceability links.
Refund requests go to the treasurer; no initial self-service bank withdrawal.

### 5.5 Profile, preferences and lifecycle

Authenticated password changes belong in Account and privacy: current password and
the new password twice, without email confirmation. Identity validates the current
password and its normal policy. Password changes and a required notification are
committed atomically, without passwords in delivery data; the current sign-in is
refreshed. Forgotten-password recovery remains a separate public flow.

Invoice-email save buttons track the saved preference and are disabled until it
changes (with the ordinary form remaining usable without JavaScript).

- Group personal/contact details, email change, communications and account/security
  actions into understandable sections.
- Distinguish current email from pending verification. The old address remains in
  use until successful verification.
- Separate newsletter and routine invoice-email preferences; required security,
  membership and deletion notices cannot be disabled.
- Resignation has a dedicated confirmation explaining immediate login/access loss,
  pending deletion and the agreed refund/retained-credit consequences. Do not imply
  it waits until prepaid membership expires.
- Pending-deletion accounts do not receive a restricted portal dashboard: existing
  sessions are blocked. Public/account flows can explain the administrator contact
  route without leaking account status to unrelated visitors.

## 6. Board administration

### 6.1 Needs attention

Prioritise verified applications, unresolved payment reviews, long-inactive members,
pending deletions, essential-mail failures and stale/failed bank synchronisation.
Show the last successful bank sync and explain when none exists.

Counts link to filtered lists. An empty queue means no known items, not proof all
integrations are healthy. Keep operational warnings separate from member standing;
scheduler internals belong in the System Console.

### 6.2 Registry and member details

Support registry search and useful lifecycle filters, including pending deletion
and inactivity duration. Make pagination/result limits clear and preserve URL
filter state. Exact inactivity thresholds remain explicit decisions, not automatic
termination rules.

Opening a registry member goes to Account overview at `/admin/members/{id}`.
Four shared member tabs provide Account overview, Details (`/details`), Billing details
(`/billing`) and Financial actions (`/actions`). A compact identity strip shows name,
member number, email and relevant account status on every member page. Details holds
profile/account maintenance and membership history; overview holds standing, balances,
subscriptions and billing settings; billing holds outstanding bills, independently
paged bill/transaction history and financial command history.

Named tasks live under `/actions/{action}` and keep Financial actions selected. Admin
bill detail stays under `/billing/invoices/{invoiceId}` and validates both the member
and bill. Preserve registry filters through all tabs, bills and tasks. A validated
`backTo` link returns completed/cancelled tasks to their originating member page;
direct task entry returns to Financial actions. Retained accounts live under Finances
and expose only their financial evidence and permitted refund/reversal tasks.

Account removal uses a confirmation dialog near the bottom of member details. Its
freeform note is optional; the command still records a standard audit reason if the
note is omitted. Restoration retains its verified-request reason.

Termination, restoration, grants and discounts are named actions with appropriate
reasons/dates/consequences, not “set active.” Restoration cancels pending deletion;
it does not promise renewed coverage, reinstated revoked access or automatic payment
repair. Retained financial evidence after erasure is not a restorable member profile.

### 6.3 Application review

Only verified applications enter review. Show submitted fields/message and relevant
verification context. Highlight under-18 applicants without automatic rejection or
a new parental-consent record workflow.

Clearly distinguish **Approve** and **Reject**. Approval establishes membership
and starts its initial period before payment. Rejection allows an optional
explanation and explains send/erasure consequences; queued does not mean delivered.

### 6.4 Payment review and adjustments

Replace ID-entry-oriented workflows with contextual selection:

1. Open a transaction; inspect source facts and review reason.
2. Explicitly choose ignore/read, or the intended member and purpose.
3. Select the relevant bill/group/balance using human-readable descriptions.
4. Review the proposed effect and required reason, then confirm.

IDs remain supporting references, not the ordinary navigation model. Never guess
a match from payer name or amount. Ignore retains a disposition rather than deleting
source facts. Explain pending-deletion and ambiguous/uncertain outcomes.

Separate **Add discount**, **Grant whole membership months**, **Credit reviewed
future discount**, **Cancel unfinished charges**, **Reverse unspent allocation**
and **Record completed refund**. Each shows only relevant fields, records and
consequences. Recording a refund does not initiate a bank transfer. No arbitrary
row editor.

Provide an effect preview where needed, but recheck facts at confirmation. Preserve
input and operation identity on validation failure. Keep reconciliation, exports
and retained claims discoverable without forcing every review through accounting
detail.

### 6.5 Communications — S3

Reserve a workspace layout for rich-text drafts, language variants, audiences,
preview and test send. Before bulk send, show recipient groups/counts, missing
variants and explicitly chosen fallbacks, then require confirmation. Distinguish
newsletters, official notices and transactional templates.

“Send now” queues delivery, not completion. Show progress, failures and uncertain
outcomes. Retry follows the safe delivery workflow, not blind resending. No initial
administrator-selected future send times or delegated newsletter permissions.
Editor/storage format/sanitisation selection remains S3 work.

## 7. Access and training — S4

Doors and machines have distinct pages/labels despite shared infrastructure. Show
resource availability and relevant eligibility requirements with actionable reasons
for denial. A portal status is not a remote unlock or a guarantee a physical reader
has the latest decision. “Unavailable” is not an emergency stop or safety lockout.

Fob management follows scan-and-claim: explain scanning, show only eligible recent
unidentified observations, then confirm registration. Members can revoke their own
current registrations. Do not expose others' fobs or broad access-event lookup.

Trainer lookup shows only name, nickname, member number and relevant qualifications.
Grant/revoke stays within the trainer's authority; no contact export, general
directory or appointment of other trainers.

## 8. Storage workspace — S5

### 8.1 Spatial and list views

Use restrained skeuomorphism to help locate physical positions: shelf outlines,
locker doors, rows/levels and readable labels such as **B-12**. Avoid decorative
3D, status-obscuring textures or a miniature game.

1. Select group/room/type, with availability and queue summary.
2. Switch between map and list of the same positions/permitted actions.
3. Inspect position/offer/rental details beside the map on desktop, below it or on
   a detail page on mobile.
4. Explicitly check out/renew, showing term, price, purchase balance and result.

Start with CSS Grid and labelled buttons for regular shelves. Use SVG only if
actual irregular geometry justifies it. Canvas, WebGL, drag-and-drop allocation
and visual layout editing are not initial requirements. Runtime group/position
administration does not imply drawing tools.

### 8.2 State and policy

Distinguish available, occupied, offered, clearance/handover, own allocation and
selection states with text/icons/borders as well as colour and a legend. Physical
clearance confirmation is independent of database allocation.

- A map does not let members bypass FIFO queues, offers or group limits. Do not
  invent free-choice allocation where policy specifies next-available offers.
  Exact position-selection interaction remains S5 design work.
- Ordinary members cannot see other renters' names/contact details. Authorised
  administration can inspect holders and continuous occupancy.
- This is not calendar booking or a recurring invoice subscription.
- Show offer/clearance deadlines and renewal limits clearly. A queue appearing
  must not make existing paid terms look retroactively shortened.
- Renewal is manual. Do not offer early-release/refund during a purchased term.
  Once the paid term ends, expose the permitted cleared/release handover action.
- Warn if physical clearance is unconfirmed; allocated does not mean empty.
- Membership inactivity blocks use/purchase, not the passage of rental time. Do
  not imply automatic cancellation, refund or handover.
- Offers reserve positions, not money. Insufficient confirmed purchase balance
  leads to deposit guidance. A promised transfer neither completes checkout nor
  extends an offer deadline.

### 8.3 Interaction contract

Use an opt-in Interactive Server component/page inside the ordinary shell. Keep a
usable list/detail alternative and keyboard operation. Native position buttons
are sufficient initially; only use an ARIA grid if its full keyboard pattern is
implemented. No pinch/hover/drag/colour-only interaction.

Selection is not reservation. Refresh availability and handle another member's
intervening allocation as an ordinary conflict. The server command atomically
rechecks membership, offer/position, term/occupancy limits and confirmed funds.

Show connection state by affected controls, leaving static navigation usable. A
lost response during submission means “outcome unknown; checking,” not necessarily
failure. Reuse the operation identity and check its result before retrying; never
silently issue another purchase. No offline checkout or client-authoritative stock.

## 9. Shared visual and interaction patterns

Use neutral surfaces, clear borders, modest rounding and readable system fonts.
Monospace suits references/technical IDs, not body copy; money benefits from tabular
numerals and alignment. Member pages are comfortable, administration denser, but
control/status meaning stays consistent. Avoid chart-heavy finance dashboards and
neon terminal styling.

Build a small vocabulary of page headings, notices, status labels, fields/errors,
action bars, pagination and confirmation summaries. Reuse actual repeated patterns,
not wrappers around every HTML element or a generic form/table framework.

### Forms and actions

- Visible labels, appropriate input types/autocomplete and nearby help.
- Validation summary plus associated field errors; retain entered values. Announce
  outcomes and manage focus after navigation/invalid submission, including enhanced
  forms.
- One clear primary action per task; separate destructive actions from ordinary saves.
- Substantial workflows use pages/inline sections, not multi-step modal editors.
- Confirmation states subject, effect, money, dates and irreversible consequences.
  Apply required reasons/confirmation without ceremony on every harmless save.
- Suppress accidental repeated clicks, but rely on server idempotency/authorization,
  not disabled buttons. Only report success after the command succeeds.
- Explain blocked actions/recovery paths in text, not disabled-button tooltips alone.

### Status and failure states

Membership, payment, subscription, resource and delivery status are different facts.
Do not compress them into one ambiguous badge. Pair labels with meaningful reasons
and dates; final translated wording can evolve without inventing domain states.

Design empty, denied, not-found and failure states where applicable. Distinguish
“no records” from “could not load.” Use progress only for actual pending work;
background import/delivery progress is not a completed business outcome.

## 10. Themes, colour modes and branding

### 10.1 Independent concerns

| Concern | Meaning |
| --- | --- |
| Association identity | Configured name/logo and page titles. Remains even when someone selects a different built-in theme. |
| Theme | Palette and restrained visual treatment: Default, Tampere Hacklab or an explicitly configured local theme. Does not change behaviour/navigation. |
| Colour mode | System, Light or Dark, independent of theme. |

Deliver two themes, **each with light and dark variants**:

- **Default:** neutral surfaces with a restrained teal/blue accent. Suitable for
  associations without custom styling; not implicitly Tampere-branded.
- **Tampere Hacklab:** black/charcoal with yellow/amber and optional orange accents.
  Dark mode uses dark neutral surfaces and bright brand emphasis. Light mode uses
  genuinely light reading surfaces with black structure and yellow/orange accents,
  not a dark page with a white form.

The deployment selects the association-default theme; never infer it from the
association name. The Theme dropdown offers **Association default**, **Default** and
**Tampere Hacklab**, plus explicitly configured local themes. Mode offers
**Light / Dark** through the adjacent sun/moon switch; **Use device colour mode**
in the Theme dropdown restores System mode independently. Initial preference: Association default
with System mode. This is a small local choice, not a marketplace or database
plugin system.

### 10.2 Palette and assets

The Tampere Hacklab logo's main colours are **black `#000000`** and **yellow
`#F9FB4C`**, as supplied by the association. These anchor the identity, not an
exclusive UI palette: use complementary/supporting colours for a balanced,
accessible interface. The UI yellow need not match `#F9FB4C` exactly; adjust it
for the colour mode, contrast and visual harmony while retaining the brand character.

For Tampere Hacklab, favour dark text on bright yellow/amber primary controls and
restrained orange secondary accents. Avoid saturated-yellow reading surfaces,
yellow text on white, and white text on bright yellow. Distinguish brand amber from
warnings using labels, icons, borders and surface treatment.

Final UI palette choices and logo assets remain implementation details; do not
present supporting colours or adjusted yellows as official logo colours. Ensure
logos remain legible in both modes,
using a neutral backing or supplied variants rather than automatic image filters.

Theme all navigation, forms, validation, tables, dialogs, focus, disabled/selected
states and future maps/editors. Dark mode is not inversion. Status meanings and
text/icon alternatives must remain clear in every variant. Print/PDF presentation
is separate and must not inherit the browser's dark background.

### 10.3 Stylesheet contract

Use semantic CSS custom properties for the supported customisation surface:

- Page/content/raised/muted surfaces.
- Primary/secondary/inverse text, borders and separators.
- Action background/text/hover/active states, links and focus.
- Success/warning/danger/information foreground/background/border pairs.
- Selected/unavailable/own-allocation map treatments as features arrive.
- A small shared spacing/radius scale and typography choices where useful.

Names such as `--nulysa-surface`, `--nulysa-text`, `--nulysa-action-bg` and
`--nulysa-focus` are illustrative, not a final public API. Map centrally to Bootstrap
variables rather than maintaining unrelated framework/custom palettes. Use explicit
root theme/mode attributes; no association-name checks or raw colours scattered
through Razor components.

Support an optional **trusted, locally deployed association stylesheet**. It can
override tokens and narrowly scoped visual rules with documented light/dark
selectors. Scope overrides to the declared theme so an association skin does not
repaint someone's selected Default theme. Document supported tokens and a small
example when implemented.

Load pinned framework/base CSS before built-in themes and applicable association
overrides. Share assets/tokens between Blazor and Razor account pages. Stable tokens
and semantic hooks are the compatibility promise; arbitrary DOM-level overrides
have greater upgrade risk.

Stylesheets/logo assets are operator-maintained deployment assets, not member
uploads, editable HTML, arbitrary remote CSS URLs or executable themes. Custom CSS
is trusted code, not sandboxed: it can conceal or misrepresent information. Server
authorization remains mandatory but does not make hostile styling safe. No secrets
or private data in public theme files, remote tracking assets/imports or mandatory
remote fonts.

### 10.4 Preferences and first paint

Proposed minimal implementation:

1. Small validated browser cookies, not new member-database preferences. Cross-device
   sync is not initial scope.
2. Resolve theme IDs against a known configured set. Invalid/removed selections
   fall back to Association default; values never become arbitrary asset URLs/paths.
3. Render known theme/mode on the initial document. For System, use CSS operating-
   system preference and a small pre-paint script if needed. Avoid wrong-theme flash.
4. Follow OS changes while System is selected; explicit Light/Dark wins. Apply
   appropriate native-control `color-scheme`.
5. Preserve appearance across enhanced/full navigation, account pages and interactive
   mounting. Changing appearance must not discard a form or repeat a purchase.

Ordinary HTTP appearance forms can be progressively enhanced for immediate updates;
no persistent server circuit is needed. Without JavaScript, core pages/forms remain
usable and System mode can follow CSS media queries. Accommodate the pre-paint
mechanism in the content-security policy rather than allowing unrestricted scripts.

## 11. Accessibility and localisation

Use **WCAG 2.2 AA as a design/verification target**, not an untested compliance
claim. Check text contrast (normally 4.5:1, large text 3:1) and relevant control/focus
indicators (3:1), alongside keyboard and screen-reader behaviour.

- Semantic landmarks, clear page headings, labels/errors and table headers/captions.
- Visible focus, logical tab order, no traps, and focus restoration for menus/dialogs.
  Drawer controls expose expanded state and suitable dismissal.
- Aim for roughly 44-pixel primary touch targets; maintain applicable AA minimums
  and spacing for denser secondary controls.
- Text enlargement/reflow at narrow widths; genuinely two-dimensional tables/maps
  use contained labelled overflow and accessible alternatives where appropriate.
  Test 320 CSS pixels and browser zoom, not phones alone.
- Reduced-motion support. No colour/hover/drag/fine-pointer-only interactions.
- Verify all four built-in theme/mode combinations. Custom association CSS needs
  its own contrast/accessibility checks.

Use resource-based strings and correct document language. Select culture through
an explicit preference/fallback policy, using the member's preferred language where
applicable and providing a public/account language control. Keep supported languages
extensible; the initial translation set, including possible Swedish, remains an
explicit choice.

Format euros, numbers and dates for the chosen locale. Finnish decimal commas must
work in input and output; browser/client and server parsing must agree. Use clear
dates with years where needed and association-local event times where appropriate.
Distinguish bank effective dates and processing timestamps when relevant.

Domain periods stay start-inclusive/end-exclusive. “Covered through …” can show the
last included calendar date; alternatively explicitly label an exclusive boundary.
Never silently change stored/invoice periods or calculations for friendlier copy.
Deletion timing reflects eligibility plus batch cadence, not an exact promised
instant.

Test long Finnish labels/messages in navigation, controls and confirmations. Avoid
English-length assumptions and untranslatable string fragments. The compact language
dropdown uses Finnish/UK flags for Finnish/English, with full language names for
screen readers and tooltips; a globe restores Automatic.

## 12. Frontend implementation approach

### Rendering and dependencies

Preserve static SSR, HTTP forms and enhanced navigation/forms where useful. Keep
working login/logout and Identity password flows on HTTP paths; consistent styling
does not require rewriting them as interactive components. No HTMX, WebAssembly,
Auto mode, client project or separate frontend authentication/API layer.

Use **Bootstrap 5.3 CSS plus a small Nulysa/association theme layer** initially.
Pin an exact maintained patch and serve assets locally when implemented. No new
frontend build pipeline merely for styling. Prefer native HTML/minimal local
JavaScript for shell/theme behaviour; add Bootstrap JavaScript selectively only
when a chosen control actually needs it.

Do not adopt a large interactive Blazor suite just for forms/navigation. Verify
SSR/form compatibility, accessibility, theming and maintenance before selecting any
widget. Choose rich-text/specialised tools at their feature boundary rather than
replacing the shell.

### Code boundaries and current touchpoints

Keep shared layouts/patterns within the existing `Components` structure, input/page
logic near its feature, and styles/scripts in normal web static assets. No upfront
assembly hierarchy or generic renderer.

- `src/Nulysa/Components/App.razor` and `Layout/MainLayout.razor`: document, appearance,
  navigation and shared shell.
- `src/Nulysa/Components/Layout/SystemConsoleLayout.razor`: distinct technical area
  without weakening authorization.
- `src/Nulysa/Pages/Shared/_Layout.cshtml`: consistent public/account presentation.
- `src/Nulysa/Components/Pages/Billing.razor`: account overview (`/services`) and billing details (`/billing`) with explicit audience/page sections.
- `src/Nulysa/Components/Pages/Payments.razor`, `ReceiptReview.razor` and
  `BillingAdministration.razor`: separate board review/import/accounting sections,
  contextual bank matching and named financial adjustments with explicit previews.
- `src/Nulysa/Components/Pages/Profile.razor`, `Registry.razor` and payment/configuration
  pages: contextual tasks and administrative hierarchy. Member/admin views can
  differ while sharing commands and appropriate display fragments.

Do not duplicate financial policy in components, JavaScript or styles. Use safe
audience-specific projections and existing named commands. Preview authorization
must match the action, and final validation remains server-side.

Interactive commands use fresh short-lived scopes/DbContexts, never a circuit-long
context. No writes in initialization/prerender. Recheck authorization/account gates
and current facts on each command. Preserve antiforgery, private-document checks,
operation keys and unknown-outcome handling.

Use bounded queries/pagination, load only the current audience's required data and
avoid fetching an entire journal for a summary card. Interactivity is feature-local,
not a live-update subscription for every status badge. Migrate pages incrementally
without replacing the domain foundation or building a separate showcase application.

## 13. Delivery status and remaining UI docket

UI work is an increment between S2 and further features; it does not renumber
S3–S6 or change the single full-replacement cutover plan.

| Increment | Current status | Remaining scope |
| --- | --- | --- |
| UI1: foundation | Delivered for current pages | Shared responsive shells, top-bar mobile menu, both themes/modes, browser preferences and local stylesheet contract are implemented. Carry their patterns forward; no additional theme framework is needed. |
| UI2: member tasks | Delivered for S1–S2 features | Homepage standing/attention, services, unified Payments, bill details, balances/transfers, independently paged histories, profile/invoice preferences and resignation are implemented. Remaining cross-cutting validation belongs to UI4, not another member-page split. |
| UI3: board tasks | Delivered for S1–S2 features | Attention dashboard, registry/member workspace, application review, contextual bank dispositions and named financial adjustments are implemented. Further cross-cutting checks belong to UI4. |
| UI4: validation/handoff | Implemented for current pages; manual handoff remains | Finnish/English resources, explicit language/fallback selection, localised money forms, focused/announced errors, bounded member billing reads and the automated four-theme responsive/contrast matrix are implemented. Supplied logos, manual assistive-technology/real zoom checks and translation review remain explicit handoff items. |
| S3–S5 UI | Future feature work | Communications, access/training and storage interfaces ship with their domain slices, not as prerequisites for completing the current member UI. |

### UI3, in bounded board-facing deliverables

1. **Attention dashboard and registry/member workspace — implemented.** `/admin`
   is the board home, with actionable queue counts and bank-sync warnings, including
   configured accounts with no recorded success. Registry lifecycle/duration filters,
   stable sorting and 25/50/100-result pages preserve search context through member
   details and payments. Both pages identify the member by name and number; details
   separate ordinary edits from lifecycle actions/history. `/admin/mail-attention`
   provides paged failed/uncertain-delivery review without unsafe resend controls.
   The dashboard's editable inactivity review filter initially uses 30 days; this
   is explicitly a view default, never a termination or deletion rule. Standing
   uses the existing evaluator and reduced membership facts, not the money journal.
2. **Application-review polish — implemented.** Compact list entries show name,
   email, municipality, birth date/age and message, with additional profile fields
   in a disclosure. Inline approval requires a visibly armed second click after a
   short debounce; without JavaScript it uses explicit confirmation. Rejection
   opens a dialog with an optional message and cancel/confirm actions. Both routes
   use the same commands, verified-only queries and safe failure feedback. Under-18
   review still uses external consent handling; no admission policy was added.
3. **Contextual payments and financial adjustments — implemented.** Bank rows open
   a source-fact detail page with explicit disposition, bounded member search and
   labelled group/bill/balance selection. Preview and confirmation preserve the retry
   key and selected record. Financial accounts offer separate discount, withdrawal,
   future-credit, whole-month grant, cancellation, refund, receipt-reversal and one-off
   actions. Only relevant fields are shown; previews freeze the entered fields before
   confirmation. Grant end dates and discount-credit amounts reuse domain calculations.
   Accounting exports, manual imports and retained balances have separate pages;
   retained financial statements/history are expandable supporting evidence. Existing commands,
   permissions and final validation remain authoritative; no uncertain external sends
   or payments gained a blind retry button.

These are presentation/workflow deliverables over already implemented features,
not a replacement billing engine or an expansion of board permissions. Test each
increment's positive paths, denial paths, validation failures and mobile operation.

### UI4: implementation and handoff

- **Localisation — implemented:** shared Finnish/English resources, `/Language` on
  public/account and portal shells, matching document/UI/formatting cultures, and
  Finnish comma/English point amount entry. Explicit browser choice wins, then the
  member's preferred communication language, browser language and configured fallback.
  Other initial languages still require a translation decision; the UI catalogue can
  grow independently of communication templates. User-entered names, reasons and
  retained financial source descriptions are preserved. Obtain native-language review
  of the translated workflows before public use.
- **Accessibility — automated coverage extended:** keyboard menu/dialog dismissal
  and focus restoration, focused/announced server validation errors, named form controls,
  focusable labelled tables, reduced-motion styles, and semantic-token contrast in
  both themes/modes. These are targeted checks, not a WCAG 2.2 AA compliance claim.
  Manual screen-reader and actual browser zoom checks remain in the handoff checklist.
- **Responsive/visual review — automated matrix implemented:** representative member
  and board pages at 320/375/768/1440 CSS pixels in all four theme/mode combinations,
  long Finnish text/association identity, contained table overflow, empty states and
  200% text enlargement. Existing journeys retain denied/failed states and contextual
  financial confirmation checks. Representative screenshots can be captured with
  `NULYSA_UI_EVIDENCE`; see [verification details](../docs/ui.md#ui4-handoff).
- **Read-query efficiency — implemented for member views:** account overview omits
  historical statements/operations; bill history and transaction pages are bounded
  in SQL independently of outstanding bills. Balances aggregate the whole journal;
  historical running balances use effective-date/ID order before newest-first display.
  Single-bill reads fetch that bill and its funding aggregates. Standing/pricing still
  use the existing evaluators. Board correction/retained-account views retain the
  existing aggregate; broader administrative profiling is future work.
- **Branding — awaiting supplied assets:** the text association identity and documented
  CSS-token/stylesheet contract remain current. Add and check supplied logos in both
  modes when available; no invented association logo or theme editor was added.
- **Acceptance evidence:** fast locale/financial equivalence tests and expanded browser
  journeys accompany the change. Record the actual gate results and remaining manual
  checks in [the handoff](../docs/ui.md#ui4-handoff). UI validation does not establish
  deployment readiness or complete the provider/privacy/cutover gates.

### Later: feature-specific UI, not current UI debt

- **S3 communications:** rich-text drafts/templates, language variants and explicit
  fallbacks, audience/test-send/confirmation, delivery progress and member newsletter
  preferences. Current invoice-email preferences are already implemented.
- **S4 access/training:** distinct door and machine views, fob claim/revoke and
  qualification-scoped trainer tools.
- **S5 storage:** group/position administration, queues/offers, map/list views,
  funded checkout, manual renewal and clearance/handover presentation.

A lightweight synthetic/read-only storage prototype can validate map/list layout,
keyboard access, theme states and mobile detail before S5. It is not a shipped
reservation feature or proof of allocation/payment correctness. Do not build the
storage domain or a drawing editor just to demonstrate the shell can hold a grid.

## 14. Acceptance and verification

Add/update tests with each increment. Keep cheap static/request tests in the fast
suite and browser/reconnect/visual journeys in the full browser suite. Follow
[AGENTS.md](../AGENTS.md) and [README checks](../README.md#checks) for prerequisites
and targeted/full-suite selection.

| Area | Required checks |
| --- | --- |
| Rendering | Ordinary pages create no persistent circuit; HTTP login/logout, antiforgery and account gates remain intact. |
| Permissions/privacy | Members cannot access others' records; trainers see only scoped data; board-only users cannot enter System Console; pending deletion blocks existing sessions. Workspace/theme changes cannot elevate authority. |
| Appearance | Both themes/both modes; OS follows System, explicit choices win; preferences survive navigation/login; invalid choices fall back; no wrong-theme flash or arbitrary asset URL resolution. |
| Local CSS | Documented token overrides work in both modes and on Razor/Blazor pages, stay in their theme scope and are not required for default functionality. |
| Responsive | Representative 320/375/768-pixel and desktop widths, long text and zoom; contained table/map overflow rather than accidental page-wide scrolling. |
| Accessibility | Keyboard menu/form/dialog use, labels/errors, navigation/submission focus, status announcements, contrast/non-colour alternatives; manual assistive-technology checks where automation is insufficient. |
| Member tasks | Active/grace/inactive/fully discounted states; partially funded combined bills; expired requests versus return workflow; available versus allocated money; transfer effects; stopped renewal retaining coverage. |
| Administration | Verified/underage review; filtered return navigation; explicit payment disposition; correction reasons; stale sync/uncertain mail without unsafe retry controls. |
| Localisation | Long Finnish strings, document language, locale-aware money/date input/output and period display consistent with exclusive boundaries. |
| Interactivity | Static shell works without a circuit; future maps/editors handle keyboard use, reconnect, stale selection, competing allocation and uncertain submission. Server concurrency tests remain feature acceptance gates. |

Existing `UiTests`, `MembershipUiTests`, `BillingUiTests` and interactive tests are
starting points, not visual/accessibility evidence. Update structural assertions
deliberately, using role/label selectors rather than fragile DOM positions. Small
representative visual snapshots may help, but do not replace financial/behavioural
assertions.

## 15. Details to settle during implementation

These do not reopen agreed direction or block foundation work:

- Final Tampere Hacklab UI palette and supplied logo assets/their use; the confirmed
  logo colours above are reference points, not an exclusive or exact-match UI palette.
- Extend the implemented CSS tokens/hooks and configuration documentation when real
  widgets need more; the local stylesheet contract and operator example already exist.
- Route and page refinements; active development does not require old-link compatibility.
- Initial translations and UI language/fallback details, without hard-coding a
  permanently Finnish/English-only model.
- Final status wording and attention thresholds where existing policy leaves them
  deliberately open.
- S3 editor/sanitisation and S5 physical-map/position-selection choices.

Themes never change policy or remove accessibility requirements. This document
does not certify public-deployment readiness: provider, privacy/retention, security
and cutover gates remain in force.
