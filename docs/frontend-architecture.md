# Frontend architecture and refactor

Owner request: review the whole frontend, modularize it and make new views easier
to build from shared templates. This is a structural continuation of AP-21 and
`uifix.md`, retaining current styling, navigation, public APIs and App workflows.

The code map and extension recipes are in
[`apps/console/src/README.md`](../apps/console/src/README.md).
Dimensions, status language and visual composition are in
[Studio layout](studio-layout.md).

## Audit and implemented changes

| Finding | Change |
| --- | --- |
| `components/` mixed domain pages, data loaders, presentation and shared controls. | Move 46 feature files into Apps, Robots, Files, Logbook, Overview, Identity, Auth and Interactions; keep tests/styles with their owners. |
| Root App combined auth recovery, workspace reads, native link handling and every route's markup. | Separate `AuthGate`, `useStudioNavigation`, `useWorkspace`, `StudioWorkspace` and one `StudioOutlet`. Root App is composition only. |
| Authentication callback construction was copied across 11 consumers. | Stable `useApiToken` hook; API transport still owns token expiry/interactive recovery. |
| App editing duplicated unload and Studio navigation guards inline. | `useUnsavedChanges`, with existing cancellation/discard behavior and parent-owned dirty state. |
| Installed Apps, Store, Logbook, Robot Detail and Organizations combined state/requests/actions with large JSX trees. | Feature controllers own those workflows. Logbook has distinct record and collection views; the install dialog is a focused Store view. |
| Startup, Live settings and release management each rendered their own primitive schema forms. | One typed `SchemaFields` renderer and shared `AppSchema`/`AppField`/`AppValues` types. Feature-specific timing, feedback, revision and save actions remain outside it. |
| Collection screens repeated the page/filter/results skeleton. | `CollectionPage` is used by Apps and Robots, built on the existing `StudioPage` grid. |
| Shared layout documentation didn't explain where new feature logic belonged. | A source-level code map, concrete controller/view examples and template recipes. |

No new server interface, schema/state transition, dependency or routing/query
framework is introduced. Public endpoint paths and request bodies are preserved.
Domain mutations remain explicit rather than being hidden behind generic CRUD.

## Extension checklist

1. Choose an existing feature, or add a feature folder when it owns a distinct
   workflow. Add the route mapping in `app/StudioOutlet.tsx`; canonical URL rules
   remain in `lib/location.ts`.
2. Select `CollectionPage`, `StudioPage`/`StudioSection`, the shared App frame or
   the history row pattern. Start with an existing feature's view/controller pair.
3. Add new reads/actions in the feature controller using `useApiToken` and existing
   typed API helpers. Reuse the same scoped read model when it already supplies
   the data. Define genuinely new API contracts before inventing endpoint calls.
4. Keep form values and mutation feedback local. Use `SchemaFields` where the App
   contract supplies a primitive schema, with unique prefixes and disabled state.
5. Keep permission explanations, exact target identities, dirty edits, polling
   cleanup and last-known snapshots intact. Do not create cross-workspace caches.
6. Exercise the changed workflow and its negative path, then run Console checks
   and relevant browser/navigation fixtures. Update architecture/layout guidance
   when introducing a pattern other features should reuse.

The timeline, artifact browser and live-setting confirmation flow remain
specialized components: their interaction, receipt and revision semantics should
not be reduced to a universal template. Shared presentation stays independent of
those state machines.
