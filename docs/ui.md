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
- `/Appearance`, available before login too. Choices persist in validated,
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
  Appearance remains in the header and is absent from profile/account settings.
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
billing features. The shared billing pages still use the existing account aggregate;
their UI pagination is not a claim of query-level financial pagination or a complete
performance rewrite. The front page and profile email settings use smaller projections.
UI3's board workflows are implemented for the current S1–S2 features. Payment review
opens `/admin/payments/{receiptId}`, shows immutable source facts and uses explicit
purpose/member/destination selection, followed by review and confirmation. Member
search returns at most 25 live members; the unresolved queue shows its oldest 100
items. Posted-receipt selection shows the latest 100; refinements to broader financial
history query efficiency remain UI4 work.

`/admin/members/{id}/actions/{action}` offers named discount, withdrawal, future-credit,
whole-month grant, cancellation, completed-refund, receipt-reversal and one-off actions.
Their relevant fields are frozen at preview, with the operation key retained through
confirmation. Domain pricing supplies the future-credit preview and the grant command
shares its end-date calculation with preview. Final commands revalidate current facts.
Payment review, manual import, accounting/exports and retained balances use distinct
pages. Retained account statements/history are supporting disclosures. Neither recording a
refund nor previewing a disposition initiates an external payment.
UI4 completes broader accessibility/localisation/visual validation; basic tests here
are not a WCAG compliance claim. Existing pages still use English, and complex
administration/history can receive further refinements during that validation.
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
