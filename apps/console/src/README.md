# Studio frontend

Start with `app/StudioOutlet.tsx` for routes, then the corresponding feature.
`App.tsx` only composes the authentication gate and authenticated workspace.

```text
app/                 Auth gate, workspace selection, navigation lifecycle, route outlet
features/
  apps/              App collection/detail tabs, startup/live settings, release management
    views/           App frame, artwork, publishers, catalog cards, install dialog
  files/             Scoped artifact browser, verified thumbnails and preview/download UI
  robots/            Robot list/detail, enrollment/credentials, capabilities and work rows
  logbook/           History controller, separate collection/record views, timeline and output
  overview/          Home and the shared bounded overview read model
  identity/          Organizations, membership/invitation operations, profile and acceptance
  interactions/      Operator decisions and their response workflow
  auth/              Sign-in presentation
components/          Cross-feature controls: headings, dialogs, time, operation feedback
  forms/             SchemaFields: one controlled primitive-schema renderer
  ui/                Basic cards/status controls
hooks/               Stable API token access and unsaved-edit guard
views/               Shared shell and page/collection/section/history templates
styles/              Shared App workflow styling
lib/                 API transport, public response types, paths, artifacts and pure helpers
```

## Build from the existing views

- Use `CollectionPage` for searchable/filterable collections (Apps and Robots).
  Supply `heading`, `toolbar`, optional `filterLabel` and results. Keep loading,
  empty, filtered-empty and error states explicit; don't fetch inside the template.
- Use `StudioPage` for a page's full-width heading and aligned content grid.
  `width="reading"` bounds Home while keeping it anchored to the same left edge.
- Use `StudioSection` for open sections. `width="form"` gives all settings the
  same form width. Existing App tabs already live inside `AppDetailFrame`; don't
  add another main/container or duplicate robot selection inside a tab.
- Use `SchemaFields` for App-declared primitive settings. Pass the schema, typed
  values, unique prefix, change handler, disabled state and optional per-field
  feedback. It preserves numeric/boolean/enum types, optional missing values,
  labels, constraints and feedback association. Save/Cancel and revision state
  belong to the feature, not to the renderer.
- Use `RunHistoryRow` and `RunStatus` for history. Use `AppArtwork`, `AppVariant`
  and `Publisher` for the same App identity across its views.

See [layout rules and sizes](../../../docs/studio-layout.md) and
[architecture/audit](../../../docs/frontend-architecture.md).

## Keep responsibilities separate

A **page** composes a template and feature views. A **controller hook** owns reads,
mutations, errors, exact installation/run/revision bindings and view state. A
**view** renders the supplied model and invokes its named actions. Examples:

| Workflow                | Controller                                       | Composition/views                                                  |
| ----------------------- | ------------------------------------------------ | ------------------------------------------------------------------ |
| Installed App tabs      | `features/apps/useAppsController.ts`             | `AppsPage.tsx`, `views/AppDetailFrame.tsx`, App tab components     |
| Store/import/install    | `features/apps/useAppStore.ts`                   | `AppStorePage.tsx`, `views/StoreInstallDialog.tsx`                 |
| Global run history      | `features/logbook/useLogbookController.ts`       | `AppLogbookPage.tsx`, `RunCollectionView.tsx`, `RunRecordView.tsx` |
| Robot inspection/action | `features/robots/useRobotDetails.ts`             | `RobotDetailPage.tsx`, `RobotCapabilities.tsx`                     |
| Membership/invitations  | `features/identity/useOrganizationManagement.ts` | `IdentityPage.tsx`                                                 |

Keep specialized rendering together where it has one responsibility, such as the
interactive timeline. Don't split every markup fragment into a generic component.
Don't add a global state store, query/router library, universal resource hook or
API cache just to create a new view. Reuse a concrete pattern first.

## Boundaries and safeguards

- Keep feature-specific components/types/tests beside the feature. Shared templates
  must not import feature controllers or API clients. Cross-feature consumers use
  deliberate public widgets/readers, such as `features/files/AppFiles`,
  `features/interactions/Decisions` and `features/overview/overview-data`.
- Import token access through `useApiToken`; `lib/api.ts` owns expiry and transport.
  Never store access tokens or authoritative domain records in a template.
- Use canonical builders from `lib/location.ts`. Workspace, robot installation,
  revision and run identity stay explicit in URLs/requests. The server enforces
  authority; client role checks only explain/disable controls.
- Keep dirty state in the owner of the edits. `useUnsavedChanges` guards unload,
  native Studio links, workspace/robot changes and browser Back/Forward through
  the existing navigation event. A canceled navigation must keep URL and values.
- Keep metadata caches inside their scoped controller. Dispose stale async results
  and polling on unmount/target change. Background updates must not erase edits or
  last-known records. Do not expand the request set when merely adding a section.
- Keep expected revision/run values and idempotency on mutations. Preserve
  read-before-download receipt verification, blob URL cleanup, fixed-run file
  scope and the run-workspace check before protected detail reads.

Tests stay beside their feature, except cross-feature route tests in `App.test.tsx`.
Run `npm run typecheck`, `npm test`, `npm run lint` and `npm run build` in Console.
Browser fixtures and navigation benchmarks in `scripts/screenshot-demo.mjs` mock
Auth0/API only in an isolated Playwright context; never add that path to product
code or treat fixture success as real account/robot acceptance.
