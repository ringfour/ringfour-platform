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

The shell uses the icon rail and mobile drawer for main navigation. The top
bar contains the organization picker and links to Organizations, System health
and My profile. Feature tabs render inside their page. Contextual back links
stay in the page heading.

Use `StudioPage` for the left-anchored content grid, `StudioSection` for open
sections and bounded forms, and `RunHistoryRow` for shared history rows. App
tabs supply sections within `AppDetailFrame`. For widths, spacing and component
examples, see the [Studio layout guide](../../../../docs/studio-layout.md).

Feature-specific App frames/artwork/catalog components now live in
`../features/apps/views`; capability and thumbnail components live with Robots
and Files. This directory contains the shared shell and templates only.
`CollectionPage` composes connected filters/results over `StudioPage` for Apps
and Robots. See [frontend source guide](../README.md) for reusable forms,
controllers, feature ownership and extension recipes.
