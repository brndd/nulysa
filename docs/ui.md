# UI foundation

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
- Basic styling of existing forms/tables and a simpler portal landing page.

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
Existing membership and billing browser journeys are retained.

UI2 will split member billing/services tasks and add actual standing/action-needed
summaries. UI3 will reorganise board workflows and replace numeric-ID-oriented
operations. UI4 completes broader accessibility/localisation/visual validation;
basic tests here are not a WCAG compliance claim. Existing pages still use English,
retain their original task structure and may need page-specific mobile refinements.
Communications, access/training and interactive storage remain their later slices.
