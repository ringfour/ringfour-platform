# Studio presentation

View structure is adapted from the owner's Lovable project:
https://github.com/cristidragomir97/ringfour-robot-studio
Source revision: 1b9976437b8c444e051e11cff097aa93e13f7061.

Keep its page composition, shared App header, cards, robot picker rows and
results-first history structure. Use Ringfour Platform's existing CSS tokens,
Auth0, contracts, exact installation identities and durable operations.
Do not copy the prototype seed/store, localStorage domain state, simulated
handshakes, generated credentials, timer-completed operations or sample results
as if they were live data. Bundled camera imagery is explicitly example output
for Store presentation; Files only displays published, verified artifacts.

The subsequent styling layer (`studio-theme.css`) follows the local website's
`packages/design/tokens.css` and `apps/web/src/{apps,product-pages}.css`:
graphite surfaces, editorial App headers, lighter heading weights, open supporting
rows and subdued controls. It preserves the supplied page composition and uses
semantic classes instead of importing website code. Craft guidance: unslop by
Damir Sokolovsky (https://github.com/heydamir/unslop), CC BY 4.0.

The owner subsequently selected the original Organizations neutral black/gray
palette for all Studio surfaces, larger sidebar labels/icons and fluid page
width. The Logbook is the explicit layout exception: its list/record presentation
is restored from Platform `4204161`, backed exclusively by current App APIs.
`../features/logbook/logbook.css` adapts the retained historical `logbook2-*` styles.

All views now use `PageHeading`, based on the Apps collection header.
`studio-chrome.css` owns its full-width spacing and common type scale; App tabs,
Timeline controls and run metadata fit its optional footer. `StudioBackLink`
keeps contextual back links in page headings; `lib/studio-chrome.ts` retains
same-workspace collection/Timeline context.

Content-heavy views follow the Organizations card spacing: section cards use
24px padding and 24px gaps, including robot details, App details and Logbook
filters/results and record panels. Every tab uses the App Store's side spacing:
content fills the available width up to 1920px and centers beyond that limit.
Shared gutters measure the shell content width, aligning headings, bodies and
top-bar controls while page header backgrounds remain full width.

Latest navigation direction: restore the 72px icon sidebar and its mobile
drawer. Main links and logo live in the rail, with hover/focus labels and
active-page feedback. The restored `StudioTopBar` contains the Organization
dropdown and Organizations, System health and My profile links. It uses the
existing guarded workspace-change callback; switching leaves detail routes for
the selected organization's collection. Timeline selects Logbook in the sidebar.
The Profile workspace picker remains available. Back links stay in page headings
and feature tabs remain inline.

Logbook collection groups use `StudioTabs` in the Runs card, after its heading
and below filters. All App/run record tabs render in place within their page;
group selection and URL filters are unchanged. App Store collection headers use the shared 80px minimum heading row,
with field margins reset in header actions. Installed Apps reuse the Store card
classes, imagery and publisher credit, with an installation-specific destination,
robot name and run status on every card.

The `uifix.md` pass uses `StudioPage` for a shared left-anchored 12-column body,
`StudioSection` for section cards and consistently bounded forms, and
`RunHistoryRow`/`RunStatus` for Activity, Logbook and device history. App tabs
return sections within `AppDetailFrame`'s existing page template. See
[the layout cookbook](../../../../docs/studio-layout.md) for sizes and examples.
This supersedes the earlier boxed App Overview/settings/activity compositions.

Feature-specific App frames/artwork/catalog components now live in
`../features/apps/views`; capability and thumbnail components live with Robots
and Files. This directory contains the shared shell and templates only.
`CollectionPage` composes connected filters/results over `StudioPage` for Apps
and Robots. See [frontend source guide](../README.md) for reusable forms,
controllers, feature ownership and extension recipes.
