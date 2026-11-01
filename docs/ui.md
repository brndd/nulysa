# Member UI and foundation

The first UI deliverable adds a responsive shell and shared presentation without
changing membership/billing commands or database schema. The complete direction
and remaining deliverables are in [the UI design](../AGENTS/UI_DESIGN.md).

## Available now

- Desktop sidebar and mobile disclosure menu, with keyboard dismissal and a
  no-JavaScript expanded-navigation fallback.
- Separate member, board-administration and System Console navigation. Workspace
  links remain permission-controlled; switching workspaces is not impersonation.
- Shared styling for Blazor pages and HTTP-based login/password/account pages.
- Default (teal/neutral) and Tampere Hacklab (black/yellow with supporting neutrals)
  themes, each supporting Light, Dark and System mode.
- `/Appearance`, available before login too. Choices persist in validated,
  HTTP-only browser cookies for one year, not in member records. Association
  default follows deployment configuration; an explicit theme overrides it.
- Local Bootstrap 5.3.8 CSS and its MIT licence; no runtime CDN/font dependency or
  global interactive circuit. Small local JavaScript handles native-control colour
  mode and mobile navigation across enhanced navigation.
- Homepage membership standing/grace/inactivity, up to five outstanding bill links
  with access to the full list, and pending email verification. Its audience-scoped
  query reuses membership evaluation without loading the full money journal.
- `/services` covers membership, subscriptions, intervals and forecasts. All member
  money tasks live on **Payments** at `/billing`; there are no separate balances or
  transaction-history routes/sidebar items.
- Payments prioritises a prominent outstanding-bills box, then one current-balance
  summary. Deposit instructions and transfers are secondary expandable tools.
  Previous bills are collapsed initially and paged separately, newest first, ten
  at a time by default. Transaction history has its own boxed area, balance selector
  and independent ten-row paging. Its newest-first display preserves the correct
  chronological running balances. Neither pager removes outstanding bills or
  changes the other history's page.
- `/billing/invoices/{id}` provides an authorised bill-detail view, item funding,
  bank-transfer instructions for open remaining amounts and PDF access. Closed or
  fully funded bills do not request payment. Displayed bill periods include their
  final day; domain dates remain exclusive.
- The board's existing `/admin/members/{id}/billing` combined view remains available
  until its separate workflow redesign. All writes still use existing commands.
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
  email preferences, password reset, privacy contact and resignation. Payments links
  to profile communication settings rather than duplicating that form. Required notices cannot be disabled;
  newsletter preferences remain part of S3, not a nonfunctional placeholder.
- One-use success notices after completed changes, stored through protected cookie
  TempData rather than trusting a success query string. No success is shown for a
  preview or rejected command. Resignation includes waiting/batch timing, loss of
  access, prepaid-time and remaining-credit consequences.

The themes use the confirmed logo colours `#000000` and `#F9FB4C` as references,
not an exclusive palette. The light variant deliberately adjusts yellow. No logo
asset is bundled, and the theme is not a claim to reproduce an official full brand
guide.

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
ten-item limits and correct running balances in newest-first views. These checks
do not constitute a full accessibility audit.

UI2's member experience is delivered for the currently implemented membership and
billing features. The shared billing pages still use the existing account aggregate;
their UI pagination is not a claim of query-level financial pagination or a complete
performance rewrite. Homepage and profile-email queries avoid that aggregate.
UI3 will reorganise board workflows and replace numeric-ID-oriented operations.
UI4 completes broader accessibility/localisation/visual validation; basic tests here
are not a WCAG compliance claim. Existing pages still use English, and complex
administration/history can receive further refinements during that validation.
Communications, access/training and interactive storage remain their later slices.
