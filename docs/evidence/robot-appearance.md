# Robot appearance verification — 26 November 2026

Scope: new optional Console-owned robot visual metadata, owner-only updates,
enrollment selection, shared artwork, uploaded raster images, and the requested
Base101 image for `virt2`. Contracts and migration are documented in
`docs/robot-appearance.md`; live steps are in
`docs/human-testing/robot-appearance.md`.

Validation completed:

- Console (React 19.2, TypeScript 5.9.3, Vitest 4.1.12): `npm run typecheck`;
  `npm test -- src/features/robots/RobotAppearanceEditor.test.tsx
  src/features/robots/RobotDetailPage.test.tsx
  src/features/robots/RobotsWorkspacePage.test.tsx
  src/components/StudioManagement.test.tsx
  src/features/overview/HomePage.test.tsx` — initial 19 cases passed; after
  adding successful image resizing coverage, the editor's 4 cases passed
  again. Enrollment checks assert Base101 for a new robot and the default icon
  when selecting an existing unregistered robot. Editor checks cover retries,
  unsupported uploads, resized image preview/persistence request, and fallback.
- Biome 2.3 checks passed on the ten changed robot/type files. The pre-existing
  string-concatenation suggestion in StudioManagement.test.tsx remains.
- Contracts: `node --test test/fixtures.test.mjs` and
  `python3 test/openapi_refs.py` passed, including appearance choices, rejected
  remote/SVG inputs, required update fields and OpenAPI references.
- API (.NET 10 / EF Core 10): `dotnet build tests/Ringfour.Api.Tests --no-restore`
  passed with zero warnings/errors. `dotnet ef migrations
  has-pending-model-changes --project src/Ringfour.Api --no-build` found none.
  The migration SQL was generated and reviewed with `dotnet ef migrations
  script 20261125150000_AP18RemovalHistory 20261126110845_RobotAppearance
  --project src/Ringfour.Api --no-build --output /tmp/rf-robot-appearance.sql`.
- VSTest could not start: `dotnet test tests/Ringfour.Api.Tests --filter
  FullyQualifiedName~RobotAppearanceTests --no-restore` aborted because the
  environment denies its test communication sockets. A temporary console
  harness at `/tmp/rf-robot-appearance-check` instead invoked the compiled API
  validator in process: seven accepted choices and nine rejected/oversized
  cases passed. This is validator verification, not database acceptance.

Backend regression coverage was added for owner-only writes, 404 for outsiders,
invalid/missing appearance, list/detail responses, persistence in a new API
instance, reset and auditing. Pairing coverage now asserts the chosen image.
These database-backed tests compile but were not executed here. PostgreSQL
migration application, real device pairing/reconnection and browser visual
acceptance remain pending. No Platform or virtual stack was rebuilt/reloaded;
the operator must reload API and Console before these changes are live.

Follow-up visual direction: Home and Robots now render the same `RobotWorkRow`
card with a large image and 28–36px title. The detail image is placed immediately
before the name. The original Base101 image has an alpha bounding box of
`(462, 186, 1577, 949)`; PIL 13.0.0 cropped a padded box
`(420, 145, 1620, 1015)` and resized to 720 × 522 with Lanczos, reducing the
bundled PNG to 185 KiB. The resulting crop was visually inspected. Source
`base101.png` is unchanged.

After this pass, TypeScript and the 12 tests in RobotAppearanceEditor,
RobotsWorkspacePage, RobotDetailPage and HomePage passed. Biome checked the
changed presentation files. Full browser layout acceptance is still pending.

Follow-up card integration: removed the legacy list wrapper around the Home
robot cards, which was overriding their side padding. Robot name/state now
form one heading line; bold Last run/Last seen labels share a compact metadata
row, with lowercase state and `just now` values. The detail header places the
image before the title/description column, keeping details under the name.
TypeScript, Biome and the eight HomePage, RobotDetailPage and
RobotsWorkspacePage tests passed. The Console reload remains operator-owned.
