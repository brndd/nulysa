# Portal and administration UI

The first UI deliverable adds a responsive shell and shared presentation without
changing membership/billing commands or database schema. The complete direction
and remaining deliverables are in [the UI design](../AGENTS/UI_DESIGN.md).

## Available now

- Desktop sidebar and mobile disclosure menu, with keyboard dismissal and a
  no-JavaScript expanded-navigation fallback.
- Separate member, board-administration and System Console navigation. Workspace
  links remain permission-controlled; switching workspaces is not impersonation.
- `/admin` is the board landing page. Queue counts link to verified applications,
  payment exceptions, inactive members, pending deletions and essential-mail problems.
  Bank warnings show the last success/attempt and configured accounts with no recorded
  sync, using the existing `Bank:StaleHours` threshold. Disabled import is explicit.
- Registry filters, sorting and pagination live in the URL. Results use stable ID
  ties and 25/50/100-member pages. Inactive duration excludes pending deletions;
  0 includes inactivity without a known start date. The dashboard starts with an
  editable 30-day review filter, tucked into the inactive-count card. It never changes
  membership or deletion policy.
  Reduced standing facts are evaluated across matching candidates before pagination;
  these queries do not load the money journal.
- Board member details and payments share a subject heading and preserve the originating
  registry search through section links, saves and lifecycle confirmation. Lifecycle
  actions and history are separate from ordinary edits. Restoration feedback explains
  that cancelling deletion does not renew coverage or repair payments.
- `/admin/mail-attention` separates failed and uncertain mail, with essential-only
  and all-mail filters and 25-result pages. Delivery/provider references support
  reconciliation; no blind retry action is offered. Invoice emails are optional
  and excluded from the dashboard's essential-mail count.
- Application list entries show the full message and basic profile information, with
  secondary fields behind a disclosure. Approval uses an armed second click after
  700ms (expiring after ten seconds); rejection has an optional-message dialog.
  Without JavaScript, approval uses a confirmation checkbox and dialogs are inline
  disclosures. Cancel/Escape closes native dialogs and returns focus to the trigger.
- Detail pages use denser controls, responsive profile fields, compact services and
  bill summaries. Email changes show the current address. Invoice-email preferences
  are available to members and administrators on profile and in the overview’s Billing settings section.
- Account removal is a short confirmation dialog with an optional note; an omitted
  note records a standard administrator-removal reason in the existing audit trail.
  Restoration still requires a reason/verified request.
- Active/upcoming discounts show fee, value and inclusive display dates. Links to
  outstanding bills come from their stored pricing segments, without repricing in
  the UI. New automatic payment exceptions distinguish outgoing transfers, unsupported
  currency, unknown references and unavailable/pending-deletion accounts. Existing
  stored review reasons are retained; reseeding development data produces new reasons.
- Shared styling for Blazor pages and HTTP-based login/password/account pages.
- Default (teal/neutral) and Tampere Hacklab (black/yellow with supporting neutrals)
  themes, each supporting Light, Dark and System mode.
- A shared **Theme** dropdown, adjacent sun/moon light/dark switch and compact flag
  language dropdown, available before login too. **Use device colour mode** restores
  System, and a globe restores Automatic language selection. `/Appearance` and
  `/Language` remain standalone preference forms. Choices persist in validated,
  HTTP-only browser cookies for one year, not in member records. Association
  default follows deployment configuration; an explicit theme overrides it.
- Local Bootstrap 5.3.8 CSS and its MIT licence; no runtime CDN/font dependency or
  global interactive circuit. Small local JavaScript handles native-control colour
  mode and mobile navigation across enhanced navigation.
- `/` is a separate high-level front page, with highlighted attention first and
  membership standing. Its small projection leaves room for future feature summaries.
- `/services` is the account overview: highlighted bill notices first,
  membership/service standing, balances and deposit/transfer tools. **Billing settings**
  groups invoice-email preferences with billing intervals. Board-only free-day grants,
  service removals and financial adjustment links stay in the administration view.
- Member navigation has a general Overview and sections for Membership & payments
  and My account. Settings is a single sidebar entry for personal/communication settings. Account
  overview and Billing details have reciprocal tabs on both pages, including mobile.
  Theme and language controls remain in the header and are absent from profile/account settings.
- `/billing` shows outstanding bills first, then separately paged bill and transaction
  histories. Current balances and transfers live on the overview. Previous bills
  are collapsed initially; each history defaults to ten rows and keeps independent
  query parameters. Historical running balances remain chronological calculations.
- Profile fields expand to four columns on wide screens. Account removal has a
  danger-coloured trigger under Account removal and restoration; history is labelled
  Membership history.
- `/billing/invoices/{id}` provides an authorised bill-detail view, item funding,
  bank-transfer instructions for open remaining amounts and PDF access. Closed or
  fully funded bills do not request payment. Displayed bill periods include their
  final day; domain dates remain exclusive.
- Board records open on Account overview at `/admin/members/{id}`. Details,
  Billing details and Financial actions have separate pages and shared member tabs.
  All writes still use existing commands.
- Compact member bill summaries; item-level funding stays on bill detail.
  History uses readable movement titles, with internal IDs/recording timestamps in
  expandable details. Available credit and allocated funds remain distinct.
- A review/confirm step for member service subscription, stop-renewal, membership
  return and balance transfers. A preview does not write or reserve funds. Final
  commands recheck authority, eligibility and availability with the original retry
  key. Interval changes retain their simpler save workflow and show their effective
  timing. Service base prices are explicitly distinguished from actual prorated/
  discounted bill amounts.
- Profile sections for personal details, verified email change, existing invoice-
  email preferences, password changes, privacy contact and resignation. Billing details links to the overview’s Billing settings. Required notices cannot be disabled;
  newsletter preferences remain part of S3, not a nonfunctional placeholder.
- One-use success notices after completed changes, stored through protected cookie
  TempData rather than trusting a success query string. No success is shown for a
  preview or rejected command. Resignation includes waiting/batch timing, loss of
  access, prepaid-time and remaining-credit consequences.

The themes use the confirmed logo colours `#000000` and `#F9FB4C` as references,
not an exclusive palette. The light variant deliberately adjusts yellow. No logo
asset is bundled, and the theme is not a claim to reproduce an official full brand
guide.

## Administration structure

The sidebar has visible Members, Finances, Communications and Association settings
groups, plus Needs attention. A shared destination definition supplies sidebar links,
section tabs and selected-parent matching. Every page below `/admin` starts with an
Administration breadcrumb, one heading, any compact record identity and appropriate
page tabs. Single-page sections omit tabs. Member records use their own four tabs;
other record pages use breadcrumbs without an extra tab row. In-page shortcuts and
action directories have separate visual treatments.

| Destination | URL |
| --- | --- |
| Member account overview | `/admin/members/{id}` |
| Personal/account details | `/admin/members/{id}/details` |
| Bills and transaction history | `/admin/members/{id}/billing` |
| Financial task directory / task | `/admin/members/{id}/actions[/{action}]` |
| Admin bill details | `/admin/members/{id}/billing/invoices/{invoiceId}` |
| Payment review / bank transaction | `/admin/payments[/{receiptId}]` |
| Manual import | `/admin/payments/import` |
| Reconciliation and exports | `/admin/payments/accounting` |
| Retained balances / financial record | `/admin/payments/retained-balances[/{accountId}]` |
| Fees / price history and dated-price form | `/admin/settings/fees[/{feeId}]` |
| Services / create / edit | `/admin/settings/services`, `/new`, `/{serviceId}` |

The `returnUrl` parameter remains restricted to registry list URLs and preserves
search/filter/sort/page state through member tabs and tasks. Task and bill `backTo`
links accept only pages within the selected member's workspace, including that
member's bill routes; invalid origins fall back to the appropriate directory/page.
Financial task confirmation returns to its validated origin with success feedback.
Retained-account tasks return to the retained financial record. The invoice route
checks the selected member's ownership even for administrators.

Service identity comes from the edit route, with no editable ID field. Service and
fee pages link to each other. Existing combined configuration/financial routes and
query-selected payment pages have no compatibility aliases.

`AdminStructureTests` checks the route/header/tab/selection matrix, missing records,
wrong-member bill contexts, admin self-view and revoked authority. Financial workflow
tests assert scoped return links and unchanged read-only preview/key behaviour.
`AdminStructureBrowserTests` exercises member/bill/action return journeys, browser
history, no-script links and 320/375/767/768/1280-pixel layouts in both themes/modes.

## Deployment configuration

Defaults require no configuration. To use Tampere Hacklab as the association skin:

```json
{
  "Appearance": {
    "DefaultTheme": "tampere-hacklab"
  }
}
```

`Appearance__DefaultTheme=tampere-hacklab` is the equivalent environment variable.
Allowed defaults are `default` and `tampere-hacklab`; invalid configuration fails
startup. Changing the default does not overwrite someone's explicit browser choice.

### Association stylesheet

Optionally set `Appearance:Stylesheet` to a root-relative local CSS path, for
example `/styles/association.css`. Add the file to `src/Nulysa/wwwroot/styles/`
**before building/publishing** so it is included in the static-asset manifest.
This is not runtime stylesheet upload or arbitrary filesystem serving. Remote URLs,
path traversal, query strings and non-CSS paths are rejected.

The file loads after the shared stylesheet on both Razor and Blazor pages. Scope
overrides to the selected base theme so other theme choices remain intact:

```css
/* Applies only when this theme is selected; retains its dark/light palettes. */
:root[data-theme="tampere-hacklab"] {
    --nulysa-radius: .25rem;
}

/* Example of a mode-specific override. Also handle System's media query below. */
:root[data-theme="tampere-hacklab"][data-mode="light"] {
    --nulysa-page: #fafaf2;
}
@media (prefers-color-scheme: light) {
    :root[data-theme="tampere-hacklab"][data-mode="system"] {
        --nulysa-page: #fafaf2;
    }
}
```

Current shared tokens: `--nulysa-page`, `--nulysa-surface`, `--nulysa-muted`,
`--nulysa-text`, `--nulysa-secondary`, `--nulysa-border`, `--nulysa-action-bg`,
`--nulysa-action-text`, `--nulysa-link`, `--nulysa-focus`, `--nulysa-danger`, and
`--nulysa-radius`. Native `color-scheme` and Bootstrap body/link/border variables
are mapped centrally. Theme-specific raw colours belong in CSS, not Razor logic.
Keep action foreground/background and focus/contrast changes coordinated.

Custom CSS is trusted operator code: it can hide or misrepresent content. Do not
include secrets, private assets, remote imports or trackers. Verify both modes and
System behaviour after overriding colours. Additional registered themes and a theme
editor are not part of this increment.

## Verification and remaining work

Fast tests cover preference validation, local-path restrictions, default fallback,
antiforgery, static rendering and shared account/portal appearance. Targeted Chromium
tests cover all four theme/mode combinations, OS mode changes, mobile menu/keyboard
dismissal, enhanced navigation, narrow-page overflow and no-script appearance forms.
Existing membership and billing browser journeys also exercise preview-before-write,
confirmed transfers, one-use feedback, profile preferences and mobile forms. Portal
query tests compare summary standing with the billing evaluator and verify audience
and deletion gates. Additional checks cover independent history paging, default
ten-item limits and correct running balances in newest-first views.

`BoardUiTests` cover queue counts, live authority checks, standing/filter consistency,
registry results beyond 100 members, mail pagination, filtered return navigation and
failed lifecycle validation without writes. `BoardBrowserTests` exercise board pages
at 320/375/768/1280 pixels in both themes/modes, mobile termination/restoration,
preserved search state, no-script navigation/forms and absence of a persistent circuit.
These checks do not constitute a full accessibility audit.

UI2's member experience is delivered for the currently implemented membership and
billing features. Member account/bill/history pages now use dedicated read projections
with SQL history pagination and journal aggregates; domain commands and board correction
views retain the existing financial evaluator/aggregate. The front page and profile
email settings also use smaller projections.
UI3's board workflows are implemented for the current S1–S2 features. Payment review
opens beneath the selected row on `/admin/payments`. The sortable table shows 25
transactions per page and defaults to payments needing review. **All payments**
includes processed transactions. The URL preserves the filter, sort and page during
purpose, member and destination selection, preview and confirmation. Member search
returns at most 25 live members. Posted-receipt selection shows the latest 100. Broader board financial-history
profiling can follow actual account sizes; it does not change the member read path.

`/admin/members/{id}/actions/{action}` offers named discount, withdrawal, future-credit,
whole-month grant, cancellation, completed-refund, receipt-reversal and one-off actions.
Their relevant fields are frozen at preview, with the operation key retained through
confirmation. Domain pricing supplies the future-credit preview and the grant command
shares its end-date calculation with preview. Final commands revalidate current facts.
Payment review, manual import, accounting/exports and retained balances use distinct
pages. Retained account statements/history are supporting disclosures. Neither recording a
refund nor previewing a disposition initiates an external payment.
UI4 implements localisation and the automated accessibility/responsive matrix described
below. Manual checks remain explicit; these tests are not a WCAG compliance claim.
Communications, access/training and interactive storage remain their later slices.

The primary responsive modes meet at 768 CSS pixels: sidebar and two balance columns
above the breakpoint; mobile menu and one balance column below it. Other panel/filter grids also stay single-column in mobile mode instead
of expanding again when the sidebar disappears. A single balance
card fills its row in either mode. Preference buttons disable when unchanged, enable
on edits and disable again on reverting/saving; the no-script form remains usable.

Settings includes a current-password/new-password/confirmation form posting to the
HTTP `/Account/ChangePassword` handler. It validates the current password and Identity
policy, refreshes the current cookie, and commits the password with a durable required
`PasswordChanged` notice in the same EF/Quartz transaction. The operation key supports
safe duplicate submissions without storing passwords in delivery records. Notification
mail is not controlled by invoice-email preferences and contains no passwords/tokens.
Failed validation returns to Settings with empty password fields and a one-use error.


## Language and financial input

The initial UI languages are Finnish and English. The shared header, including
public and account pages, contains a flag dropdown: Finnish/UK flags select
Finnish/English, and a globe selects Automatic. The flag controls have full language
names as accessible labels and tooltips. `/Language` also remains available as a
standalone form. All choices use antiforgery-protected HTTP forms and return to the
current local page, preserving its query string. The one-year `Nulysa.Language` cookie is browser-specific,
HttpOnly, SameSite=Lax and Secure on HTTPS. It does not update account data or authority.
Select **Automatic** (the globe in the dropdown) to clear it. Invalid cookie values are ignored.

Selection order is explicit browser choice, authenticated member's preferred language,
browser `Accept-Language` quality order, then `Ui:DefaultLanguage`,
`Membership:DefaultLanguage`, or English. Header variants such as `fi`, `fi-FI` and
`en-US` resolve to matched UI/formatting pairs (`fi`/`fi-FI`, `en`/`en-GB`). The document
`lang` and response `Content-Language` reflect the selected UI language. Changing a
member's preferred language takes effect on the next request when Automatic is used.
A browser choice continues across navigation, login and logout.

For example, `Ui__DefaultLanguage=fi` selects Finnish for visitors whose browser has
no supported language. The initial languages are a catalogue in
`Localization/UiLanguages.cs`, not a two-language framework limit. To add a UI language,
add its culture/native name to that catalogue and a `UiStrings.<code>.resx` translation.
The English base resource is the per-message fallback. Communication-language support
and invoice/mail templates are a separate domain configuration; adding a UI translation
does not silently claim translated mail. Stored names, service/fee names, administrator
reasons and source financial descriptions remain as recorded.

`UiStrings.resx` and `UiStrings.fi.resx` contain presentation phrases, using whole
messages and format arguments where ordering can vary. Both Razor Pages and static
Blazor pages use them. Validation summaries translate framework field errors and
Identity password-policy messages, announce errors and receive focus on failed HTTP
submission. English-copy regression journeys explicitly select English; locale journeys
exercise automatic/member/browser selection separately.

Money fields use text inputs with `inputmode=decimal`. Finnish accepts `1,25`; English
accepts `1.25`. Grouped, exponential and wrong-separator values are rejected rather
than reinterpreted as a different amount. Review/confirmation hidden values use the
same culture. Domain commands still validate positive/cent amounts, available funds
and current authority. Native date controls continue to submit ISO dates; visible
periods use the selected culture and inclusive last-day wording without changing
stored exclusive boundaries. The browser controls its native date-picker chrome.

## Member financial read paths

`BillingQueries.Portal` loads current membership/service facts and the requested bill
page, aggregates available/allocated balances from the full journal in SQL, and omits
historical operations/statements from the account overview. Transaction pages fetch
at most one page plus a continuation row per balance. Earlier effective-date/ID deltas
supply each page's opening balance; running balances are calculated in chronological
order before display is reversed. Page-specific supporting receipts/lines/operations
retain the authorised traceability links. Outstanding bills remain outside historical
pagination. A single-bill page loads only that bill, its reference and funding aggregates.

Standing, grace, discount and forecast calculations use the existing evaluator and
signed journal deltas. `PortalBillingQueryTests` compare the member projection with the
original financial view, including backdated/same-date events, independent pages,
retained outstanding bills and single-bill funding. No database schema, command,
authorisation rule or financial policy was changed. Price/configuration lists and
current outstanding requests are not artificially truncated; board financial corrections
and retained financial accounts still use the original aggregate.

## UI4 handoff

`UiValidationBrowserTests` checks member and board pages at 320, 375, 768 and 1440 CSS
pixels in Default/Tampere Hacklab × light/dark: long Finnish text, one page heading,
no page-wide overflow, named board form controls, semantic text/action/focus/control
contrast and no persistent circuit. It also checks 200% text enlargement, keyboard
menu/dialog use, focus restoration, reduced-motion preference and server-error focus.
Existing journeys cover empty, denied, failed and successful workflows, payment
previews, no-script forms and appearance preferences. `LocalizationTests` adds a real
Finnish comma transfer through HTTP review/confirmation, invalid input without writes,
antiforgery, safe return URLs and language/permission isolation. Unit tests check
resource format arguments, browser-region matching and strict numeric parsing.

Capture representative screenshots when running the matrix:

```sh
NULYSA_UI_EVIDENCE=/tmp/nulysa-ui4-evidence dotnet test --settings tests.full.runsettings --filter 'FullyQualifiedName~UiValidationBrowserTests'
```

Acceptance run on 2026-11-02: the full Release suite passed **262 tests** (4m46s),
including the expanded browser matrix and long-history financial equivalence case.
The subsequently added Finnish board price/ISO-date regression passed separately.
The Release build, `dotnet format --verify-no-changes --no-restore`, the three Python
development-tool tests and the isolated production-container web/worker smoke check
also passed. Representative desktop/mobile screenshots were inspected in all four
built-in theme/mode combinations. Manual checks below remain outstanding.

The screenshots are local review artifacts, not image-diff baselines. The built-in
palette passes the tested token pairs; association CSS overrides require their own
review. The CSS contract above has not changed. No association logo assets have been
supplied, so the association's text identity remains the shipped treatment.

Before calling the handoff manually verified:

- Review the Finnish translations, especially financial confirmations and membership
  lifecycle messages, with a native-speaking association volunteer.
- Use a screen reader (for example NVDA/Firefox and VoiceOver/Safari) through login,
  navigation, a form error, application rejection, a transfer and a financial preview.
  Check announcement order, labels, table relationships and restored focus.
- Check actual browser zoom at 200% and 400%, plus mobile keyboard/landscape operation.
  Automated text enlargement and 320-CSS-pixel reflow are evidence, not a substitute
  for these browser/assistive-technology combinations.
- Supply the association logo, then verify its alternative text and light/dark
  legibility. Recheck any operator stylesheet against the same contrast/focus pairs.

UI4's implemented checks do not certify WCAG compliance or public deployment readiness.
The remaining manual/asset-dependent items are handoff requirements, not hidden tests
or new S3–S5 feature scope.


### Compact header controls

The header's Theme dropdown submits only the theme preference; the adjacent sun/moon
switch submits only the colour mode. Neither resets the other choice. The switch's
checked state follows the effective OS preference while System is selected; activating
it sets an explicit light/dark preference. **Use device colour mode** in the Theme
menu returns to System. Native icon buttons for Light/Dark remain available without
JavaScript. Country flags are local inline SVGs rather than platform-dependent emoji.

Native disclosure menus work with keyboard Tab/Enter. The small local script also
closes the other dropdown, dismisses on outside click/Escape, and restores focus to
the triggering summary on Escape. These controls do not start an interactive circuit.
`HeaderPreferencesTests` cover independent cookie updates, allowlists, antiforgery and
safe return URLs. Browser tests exercise both shells, OS preference changes, keyboard
navigation, narrow layouts, enhanced navigation and the no-script forms.

Header polish verification (2026-11-02): 41 targeted fast appearance/localisation/
request tests and five browser tests passed, including the four-theme responsive
matrix. Keyboard dismissal/focus restoration, independent choices, OS mode changes,
enhanced-navigation switch state and no-JavaScript controls are covered. Desktop
and 320-pixel screenshots were inspected; formatting and diff checks passed.


### Interface copy

Keep labels short and use everyday language in Finnish and English. Put actions on
buttons (for example, “Change billing period”); explain only consequences that matter
to the decision. Avoid descriptions of internal processing, repeated explanations of
headings and defensive caveats on ordinary status messages. Keep payment and access
consequences in confirmations. Upcoming invoices use that heading rather than
“forecasts”. Service lists show the standard monthly price; subscription confirmation
explains discounts, free days and automatic renewal.

Resource identifiers retain their original English phrases because domain errors and
stored feedback also use them. Edit the English and Finnish resource values together;
the identifier is not necessarily the displayed copy.
