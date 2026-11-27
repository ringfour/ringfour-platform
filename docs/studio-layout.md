# Studio layout template

`uifix.md` in the workspace root governs this presentation pass. Keep the shared
Apps header and selected robot, icon rail, top-bar page tabs, Profile workspace controls, real API
operations and existing unsaved-edit guards. The visual references are Home and
Files. This extends the Ringfour system; it does not define a new API.

Use `apps/console/src/views/StudioPage.tsx` for new or revised views. It provides
full-width page chrome and a **left-anchored 12-column content grid**. Body items
span all columns by default. Page headers, section headings, forms, filters
and rows share their left edge. Never center a form or add a second page gutter.

```tsx
<StudioPage heading={<PageHeading title="Subject" description="Current context" />}>
  <StudioSection title="Configuration" titleId="configuration" width="form">
    <form className="studio-form">{/* persistent labels, fields, own feedback/actions */}</form>
  </StudioSection>
  <StudioSection title="History" titleId="history">
    <div className="studio-filter-bar">{/* labels/controls, muted loaded count */}</div>
    {/* loading, unavailable, empty, filtered-empty or real rows */}
  </StudioSection>
</StudioPage>
```

`CollectionPage` supplies the same page/filter/results skeleton for Apps and
Robots. Feature ownership, controller/view examples and controlled schema forms
are documented in [the frontend source guide](../apps/console/src/README.md).

The App frame (`features/apps/views/AppDetailFrame.tsx`) already supplies `StudioPage` and `PageHeading`; App tabs return
sections only. Do not add another main/container around a tab. Startup and Live
settings both use `StudioSection width="form"`. The selected robot and installed
release remain in the App header. Narrow forms stay anchored left.

| Primitive/token | Use |
| --- | --- |
| `CollectionPage` | Page heading, connected filter toolbar and collection content; no embedded data fetching. |
| `StudioPage` | Full-width header plus body grid; `width="reading"` caps Home at 1120px without centering it. |
| `StudioTabs` | Places page navigation links in the top bar. The feature supplies the route and the selected tab. |
| `StudioSection` | Open heading, optional description/actions and body; a preceding section adds a thin divider. |
| `--studio-form-width: 640px` | Shared Startup/Live settings width, clamped by the viewport. |
| `--studio-row-gap: 24px` | Shared column spacing; 16px on smaller layouts. |
| `--studio-row-padding: 20px` | Common vertical rhythm for operational/history rows. |
| `RunHistoryRow` | Activity, Logbook and device history share identity, outcome, time, event count and trailing Open run columns. Expandable events indent beneath their row. |
| `RunStatus` / `runStateLabel` | Starting, Running, Completed, Stopped, Failed, Interrupted. Protocol values stay unchanged. |
| `studio-filter-bar` | Bottom-align labelled fields and actions; counts sit beside filters. |
| `studio-open-table` | Unframed operational tables with bounded column spread and internal horizontal scrolling. |
| `studio-diagnostics` | Compact responsive labels/values; no surrounding card. |
| `AppArtwork`, `AppVariant` | Shared App artwork, broken/missing-image fallback and publisher/key distinction for duplicate names. |

Most content is open: use spacing and bottom dividers, not raised panels. Raised
surfaces belong to Store collections, image previews and dialogs. Feedback stays
near its action/control. Revision IDs belong in disclosures. Report loaded/partial
counts without implying a complete total. Never derive a live setting's confirmed
value from a pending request. Unknown/stale inventory is distinct from a confirmed
empty inventory; only reported capabilities/instances are displayed.

At narrow widths, sections/forms fill the available width, action groups wrap,
history rows keep their identity/outcome/time/action in a stable order, and tables
scroll inside their own region. Keep focus visible and native links intact.
`studio-content.css` owns these rules; avoid new page-specific max-width/margin
schemes. Existing secondary views may migrate to this template as they change.

Craft guidance: [unslop by Damir Sokolovsky (@heydamir)](https://github.com/heydamir/unslop),
CC BY 4.0; Ringfour's palette, typography, geometry and owner instructions win.

`components/forms/SchemaFields.tsx` is the common controlled renderer for startup,
live and release forms. Its feature supplies values, disabled state, save actions
and pending/confirmed feedback. See [frontend architecture](frontend-architecture.md).
