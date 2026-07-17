# Handoff: Eligibility Processing — Dashboard redesign

## Overview
A redesign of the main **Dashboard** (Home/Index) of the Eligibility Processing
web app — the operator's at-a-glance view of pipeline health while batches run.
Goals of the redesign: more graphical (gauges, donut, progress ring, sparklines),
a tighter single-screen layout that fits a 1920-wide desktop **with no scroll**,
icon+label controls, and a skeleton loading state to cover the 1–2s while stats
resolve on page load.

## About the Design Files
The file in this bundle (`Dashboard redesign.dc.html`) is a **design reference
created in HTML** — a prototype showing the intended look and behavior. It is
**not production code to copy directly**. The current app is **ASP.NET Core MVC
(Razor / `.cshtml`) styled with Bootstrap 5.3** (see the original
`Views/Home/Index.cshtml` and `Views/Shared/_Layout.cshtml`). The task is to
**recreate this design in that existing environment**, reusing Bootstrap 5.3's
theme-aware utilities and the app's established patterns (SignalR live feed,
`data-bs-theme` light/dark, the existing `POST /Trigger` forms). The prototype
uses a small custom design-component runtime and inline styles purely to make it
viewable standalone; ignore that runtime — map the layout and visuals onto Razor
partials + Bootstrap.

## Fidelity
**High-fidelity.** Colors, typography, spacing, gauges, and the loading motion
are final and specified below. Recreate the UI closely, but express it through
Bootstrap 5.3 components/utilities and the app's existing CSS variables
(`--bs-*`) rather than the prototype's raw hex — the hex values below are the
design intent; prefer the theme-aware Bootstrap token where one exists.

## Layout (single screen, no scroll — target ≥1920×1080)
Two-region shell inside a full-height flex column:

```
┌──────────────────────────────────────────────────────────────┐
│ [rail] │  Header: title + toolbar (icon+label controls)       │
│  66px  ├──────────────────────────────────────────────────────┤
│  icon  │  ┌ left content (flex:1) ─────────┐ ┌ live tracker ─┐ │
│  nav   │  │ Most recent run (ring + grid   │ │  width 392px  │ │
│        │  │   + resolution sparkline)      │ │  scrolls      │ │
│        │  │ [resolution gauge][fail donut] │ │  internally   │ │
│        │  │ [successful][rows][tokens×2]   │ │               │ │
│        │  └────────────────────────────────┘ └───────────────┘ │
├────────┴──────────────────────────────────────────────────────┤
│ Footer (full width, pinned to bottom, border-top)              │
└──────────────────────────────────────────────────────────────┘
```

- **Left icon rail** — `width:66px`, `flex:none`, `background:var(--surface)`,
  `border-right:1px solid var(--border)`. Vertical stack, `padding:16px 0; gap:6px`,
  centered. Logo 32×32 at top (`margin-bottom:14px`); 6 nav items as 44×44
  rounded (`border-radius:11px`) icon buttons; active item filled `--primary`
  with white icon, others `color:var(--muted)`; user avatar (34×34 circle) pinned
  at the bottom (`flex:1` spacer above it).
- **Main column** — `flex:1`, flex column. Header row then a content row
  (`display:flex; gap:13px; padding:12px 22px 20px; flex:1; align-items:stretch`).
- **Content row** = left content column (`flex:1`, flex column, `gap:13px`) +
  right live-tracker card (`width:392px; flex:none`).
- **Footer** — sibling of the shell's content row, so it spans the full width
  (under the rail too) and pins to the bottom via the `min-height:100vh` flex
  column.

### Header
- Left: title **"Pipeline overview"** (19px/700) + subtitle
  **"Eligibility Processing · live"** (12.5px, `--muted`).
- Right: the control toolbar (see Components). `padding:16px 22px 4px`.

## Components

### Cards (shared)
- `.epcard`: `background:var(--surface)`, `border:1px solid var(--border)`,
  `border-radius:13px`, `position:relative` (to host the skeleton overlay).
- Section labels `.epk`: 11.5px, `color:var(--muted)`, `letter-spacing:.01em`.
- Big metric numbers: 23–30px, `font-weight:700`, tabular-nums for aligned digits.

### Toolbar controls (icon + label)
All buttons: `border-radius:8px`, 13px/500, `gap:7px` icon↔label, `white-space:nowrap`.
Left→right:
1. **Trigger Earliest** — grouped control: a rounded surface pill
   (`border:1px solid var(--border); border-radius:10px; padding:5px 6px 5px 11px`)
   holding a count label (**"10"**) + a solid button
   (`background:var(--primaryDark)`, white text, skip-back icon, label **"Earliest"**).
2. **Trigger Latest** — same pill, count **"100"**, skip-forward icon, label **"Latest"**.
3. **Run Trial** — same pill with an **NCT ID** text input
   (`width:118px`, value placeholder `NCT12345678`) + an outline-primary button
   (transparent bg, `border-color`/`color` = `--primary`, play icon, label **"Run Trial"**).
4. **Cancel** — outline-danger button (`border-color`/`color` = `--danger`,
   x-in-circle icon, label **"Cancel"**).
5. **Reload stats** — 34×34 icon-only button (`.epico`), circular-arrow icon.
   Re-triggers the loading state.
6. **Toggle theme** — 34×34 icon-only button, sun icon. Flips light/dark.

`.epico`: `width/height:34px`, `border-radius:9px`, `background:var(--surface)`,
`border:1px solid var(--border)`, `color:var(--muted)`, `:hover` → `--text`.

### Most recent run (top-left card)
- Header line: **"Most recent run"** (14.5px/700) + a **RUNNING** badge + the
  run id as `<code>` right-aligned in `--primary`
  (`1595ec3d-2168-4179-b546-c4e93efda67f`).
- **RUNNING badge** `.epbadge`: `background:var(--info)`, text `#04222a`, 700,
  `border-radius:20px; padding:3px 11px`, with a 6px dot that pulses
  (`eppulse` 1.4s infinite, opacity 1→.3→1).
- **Progress ring**: 132px circle, `conic-gradient(var(--primary) 0 40%,
  var(--surface2) 0)`; inner 100px hole = `--surface`; center shows **40%**
  (26px/700) over **"4 / 10 studies"** (11px, `--muted`). Percent = studies
  processed ÷ study count.
- **Stat grid** (3 cols, `gap:12px 20px`): Started `10:43:55 UTC`, Trigger source
  `rerun`, Rows persisted `138`, Resolution rate `89.5%` (in `--success`),
  Time / study `00:52`, Est. finish `10:49 UTC`. Each = `.epk` label + 13.5px/600 value.
- **Resolution sparkline**: full-width, `border-top` divider above; label
  "Resolution rate — last 7 runs" + delta "+1.4pp" (`--success`); an SVG
  `<polyline>` stroke `--success`, `stroke-width:2`, `height:32px`.

### UMLS resolution gauge (card)
Column, centered. `.epk` label "UMLS resolution" top-left. 120px donut gauge:
`conic-gradient(var(--success) 0 90.2%, var(--surface2) 0)`, 88px `--surface`
hole, center **90.2%** (24px/700) over "resolved" (10px). Footer
"▲ +1.4pp vs last run" (11px/600, `--success`). The `90.2` = resolution rate %.

### Failures by type (donut card)
- `.epk` label "Failures by type".
- 104px donut: `conic-gradient(var(--danger) 0 21.4%, #b57ee5 21.4% 32.1%,
  var(--warn) 32.1% 99.5%, var(--info) 99.5% 100%)`; 62px `--surface` hole;
  center total **1,192** (16px/700) over "total". Segments are proportional to
  the counts below.
- **Legend** (11.5px, `line-height:1.8`), each row = colored 8px square + a
  **hyperlink label** + a **hyperlink count**, both routing to the History tab
  filtered by that status (`/History?status=<code>`):
  - `--danger` **Invalid JSON** → `255` (status `parse_invalid_json`)
  - `#b57ee5` **LLM Failed** → `128` (status `llm_failed`)
  - `--warn` **Parse Empty** → `803` (status `parse_empty`)
  - `--info` **No Embeddings** → `6` (studies without embeddings; not linkable in
    the original app — decide per backend, default non-link)
  - Link style: label `color:var(--muted)`, count `color:var(--text)/700`, both
    `text-decoration:none`; on hover → `color:var(--primary)`, label underlines.

### KPI tiles row (`grid-template-columns:1fr 1fr 2fr; gap:13px`)
1. **Studies successful** — `.epk` + **287,670** (23px/700) + a 5px progress bar
   (`--success` fill at 49% on `--surface2` track) + "~304,281 remaining" (10.5px).
   Bar % = successful ÷ (successful + trials remaining).
2. **Rows persisted** — `.epk` + **4,032,035** + a `--primary` sparkline
   (`<polyline>`, `stroke-width:2.5`, `height:20px`).
3. **Tokens used** (double-width, spans 2 tracks) — flex row: an 88px **pie**
   (`conic-gradient(var(--primary) 0 41.7%, var(--warn) 0)`; input vs output share)
   beside a 3-column grid (`auto 1fr auto`, `gap:5px 14px`, 12.5px):
   - `--primary` square **Input** · `418.2M` · `$2,090.95`
   - `--warn` square **Output** · `583.7M` · `$14,592.48`
   - **Total** · `1.00B` · `$16,683.42` (700, `border-top` divider above the row)
   Costs: input tokens ÷ 1e6 × $5; output tokens ÷ 1e6 × $25.

### Live activity (right card, width 392px)
- Header: activity icon + **"Live activity"** (13px/600).
- Scrollable list (`overflow-y:auto`, `flex:1`), `gap:10px`. Each event is a
  **single line** (`white-space:nowrap`): monospace timestamp (10.5px, `--muted`)
  + a type badge + description (`--muted`).
  - **TRIAL** badge success = `background:var(--success)`, text `#04220f`;
    failed = `background:var(--danger)`, white text.
  - **BATCH** badge = `background:var(--info)`, text `#04222a`.
  - Badges: `border-radius:5px; padding:1px 6px; font-size:10px; font-weight:700`.
- Card stretches to the full height of the left content stack (flex `align-items:stretch`).
- Fed by the existing SignalR hub (`/hubs/progress`): `TrialStarted`,
  `TrialCompleted`, `BatchStarted/Completed/Cancelled`, `ToolJob*`. Prepend new
  events; cap the list length.

### Footer
Centered row, `border-top:1px solid var(--border)`, 12px `--muted`, `gap:9px`
with dim `·` separators:
`© 2026 - Eligibility Processing · v0.1.22 (2026-07-17) · Release notes`
where **Release notes** is a link (`--primary`, underline on hover) to `/ReleaseNotes`.

## Interactions & Behavior
- **Loading state (the chosen "2b" skeleton shimmer).** On page load — and
  whenever the Reload button is pressed — every stat card shows a skeleton
  overlay for **~1500ms**, then the overlay fades out over **450ms** to reveal
  the real content underneath.
  - Overlay `.ep-skel`: absolutely positioned (`inset:0`), `background:var(--surface)`,
    same `border-radius:13px`, `opacity:0` by default, `transition:opacity .45s ease`,
    `z-index:3`, laid out to mirror the card's real shapes (bars + circles).
  - Shown by toggling a `data-loading="1"` attribute on the root
    (`.ep[data-loading="1"] .ep-skel{opacity:1}`).
  - **Shimmer bars `.ep-bar`**: `linear-gradient(90deg, var(--skel) 20%,
    var(--skelhi) 40%, var(--skel) 60%)` with `background-size:200% 100%` and
    `animation: epshimmer 1.3s linear infinite` (`background-position` -150%→250%).
    Use circles for the ring/gauge/donut/pie placeholders.
  - In the real app, drive `loading` off the actual data fetch (show skeleton
    until the dashboard metrics + most-recent-run queries resolve) rather than a
    fixed timer; the 1500ms here just simulates it.
- **Theme toggle**: flips light/dark. In the app, reuse the existing
  `data-bs-theme` mechanism from `_Layout.cshtml` (persisted in `localStorage`);
  the prototype's per-root data-attribute is only a stand-in.
- **Trigger / Run / Cancel**: wire to the existing `POST /Trigger`,
  `/TriggerRecent`, `/Rerun`, `/Cancel` actions; disable all trigger buttons
  while a run or tool job holds the run gate (mirror the existing SignalR
  enable/disable logic). Cancel confirms before posting.
- **Failure counts / labels**: links to `History` filtered by status.
- **RUNNING badge dot**: `eppulse` opacity pulse, 1.4s infinite.

## State Management
- `theme`: `'dark' | 'light'` — persisted (localStorage in the app).
- `loading`: `boolean` — true during the metrics fetch; gates the skeleton overlay.
- Dashboard metrics + most-recent-run summary: fetched server-side (Razor model)
  or via an endpoint; the numbers above are sample data.
- Live activity: streamed via SignalR; keep a bounded in-memory list.

## Design Tokens
CSS custom properties (dark / light):
- `--bg`: `#16191d` / `#e7ebef`  (page background)
- `--surface`: `#282e35` / `#ffffff`  (cards)
- `--surface2`: `#333a43` / `#eef1f4`  (tracks, ring remainder)
- `--skel`: `#333a43` / `#dde3e9`  ·  `--skelhi`: `#4a535e` / `#ccd5de`  (skeleton)
- `--border`: `rgba(255,255,255,.11)` / `rgba(0,0,0,.1)`
- `--faint`: `rgba(255,255,255,.06)` / `rgba(0,0,0,.04)`  (inner dividers)
- `--text`: `#e9ecef` / `#1a2028`  ·  `--muted`: `#98a2ad` / `#5c6670`
- `--primary`: `#4a9eff` / `#1b6ec2`  ·  `--primaryDark`: `#1b6ec2`
- `--success`: `#2ec16a` / `#1a9d55`  ·  `--danger`: `#e5556a` / `#d63a52`
- `--warn`: `#f5b544` / `#d98a15`  ·  `--info`: `#22c9e8` / `#0aa5c2`
- `--teal`: `#63aea2` (brand mark)  ·  LLM-failed slice: `#b57ee5` (both themes)

Radius: cards 13px · icon buttons 9px · control pills 10px · nav icons 11px ·
badges 5px · pill badges 20px.
Spacing: page padding 22px · card padding 13–18px · grid/flex gaps 13px (cards),
gap 20–22px inside the run card.
Type: system UI stack (`-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto,
system-ui, sans-serif`); monospace (`ui-monospace, Menlo, monospace`) for
timestamps, run id.
Fonts map cleanly onto Bootstrap's default; the current app has no custom font.

## Assets
- `logo.svg` — the "Eligibility Processing" platform mark (teal medical cross,
  `#49888e`/`#63aea2`). Copied from the repo:
  `contexts/eligibility/src/EligibilityProcessing.Web/wwwroot/images/PlatformLogo.svg`.
- All other glyphs are inline SVG icons (feather-style, 1.5–2px stroke,
  `currentColor`): dashboard grid, play, skip-back/forward, history clock,
  analysis chart, results, tools wrench, activity pulse, clock, coins, check,
  alert-triangle, database, x-circle, refresh, sun. Substitute with the app's
  existing icon set (e.g. Bootstrap Icons) — do not ship these literal paths.

## Files
- `Dashboard redesign.dc.html` — the high-fidelity prototype (open in a browser
  to see layout, both themes via the sun button, and the skeleton loading via
  the reload button).
- `logo.svg` — brand mark used by the design.

### Original source this recreates (in the ClinicalEligibility repo)
- `contexts/eligibility/src/EligibilityProcessing.Web/Views/Home/Index.cshtml`
  (current dashboard markup + SignalR script)
- `contexts/eligibility/src/EligibilityProcessing.Web/Views/Shared/_Layout.cshtml`
  (nav, theme bootstrap, footer)
- `contexts/eligibility/src/EligibilityProcessing.Web/Models/DashboardViewModel.cs`
  (`DashboardMetrics`, `RunMetrics` field shapes)
