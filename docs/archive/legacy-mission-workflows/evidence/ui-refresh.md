# Console visual refresh — 2026-11-20

## Scope and references

Visual and interaction polish of the existing development Console, informed by
`ui-remix.md`, Overview and Logbook, and the updated sibling website's
`packages/design/tokens.css`, `primitives.css`, and `apps/web/src/product-pages.css`.
No API, persistence, authorization, runtime or deployment contracts changed.
This is not completion of the information architecture specification or a new
milestone acceptance gate. Apps/Robots/Runs routing, run tabs, ownership/progress
extensions, gallery collection navigation and other broader remix work remain.

Kept the existing vendored Space Grotesk and packaged Inter fonts, neutral
#080808/#111111/#191919 surfaces, #e47a00 actions, teal observations and red
faults. No imports from sibling applications. Changed heading/label hierarchy,
layout density, quiet status markers, responsive navigation and table scroll
containment. Settings now group workspace, health and profile links. Cancellation
has a neutral treatment. Image dialogs render through a body portal, contain
keyboard focus, lock background scrolling and restore focus when closed.

The repository already contained Console, dependency, Runner and documentation
edits at the start. They were preserved. No dependency installation or version
change was needed for this refresh. The two failing existing App assertions
expected a zero-argument Auth0 call and obsolete organization/member markup;
they now assert the existing empty-options call and current accessible markup.

Design review used the locally installed Unslop skill by Damir Sokolovsky
(@heydamir), CC BY 4.0. No generated images were needed.

## Environment and exact checks

Working directory for these commands: `apps/console`.

- Node 24.22.3, npm 11.19.1.
- React/React DOM 19.3.1, TypeScript 5.9.3, Vite 7.3.7, Tailwind 4.3.5.
- Vitest 4.1.12, jsdom 27.4.0, Testing Library React 16.3.3,
  user-event 14.6.1, Biome 2.6.6.
- Auth0 React 2.30.0, Inter 5.3.0.

```sh
node --version
npm --version
npm ls --depth=0
npm test
npm run build
./node_modules/.bin/biome check src/App.tsx src/App.test.tsx src/components/ArtifactGallery.tsx src/components/ArtifactGallery.test.tsx src/components/DashboardPage.tsx
npm run lint
```

Outcomes:

- All 10 test files / 27 tests passed. Existing auth, permission, failure and
  control-path component tests remain in the suite. The image test additionally
  asserts body-portal placement, scroll lock, initial focus, forward/backward
  focus wrapping, Escape close, scroll restoration and return to the opener.
- TypeScript and Vite production build passed. Production retains the existing
  development-only guard.
- Biome check of all changed TypeScript files passed.
- Full `npm run lint` remains failing on formatting in untouched files; CSS also
  reports specificity/important-style warnings. This is not recorded as passing.
- `git diff --check` passed from the repository root.

## Browser inspection

Started the real Vite development server with:

```sh
VITE_AUTH0_DOMAIN=visual-test.invalid VITE_AUTH0_CLIENT_ID=visual-test npm run dev -- --host 127.0.0.1 --port 5178
```

A temporary Playwright harness at `/tmp/rf-console-refresh/review.cjs` intercepted
Auth0 and API responses only in the browser to supply clearly synthetic robot
and mission records. No authentication bypass was added to application code.
Executed from the repository root:

```sh
node /tmp/rf-console-refresh/review.cjs final
```

Chromium used Playwright from the existing npm cache and the already available
library directory `/tmp/rf-sdk-review/libs/usr/lib/x86_64-linux-gnu`.

Observed:

- Overview document width equals viewport width at 320, 390, 768 and 1440px.
  Wide tables scroll inside their containers. A positioned scroll container
  prevents absolutely positioned screen-reader labels extending page width.
- The mobile Menu opens navigation; choosing Missions closes it and opens the
  existing Missions view.
- Desktop Overview and Logbook and mobile Overview were inspected visually.
- Temporary screenshots: `/tmp/rf-console-refresh/final-desktop.png`,
  `/tmp/rf-console-refresh/final-mobile.png`, and
  `/tmp/rf-console-refresh/final-logbook.png`.

This is fixture-based browser inspection, not integrated acceptance. Real Auth0,
Platform API/Runner, restart recovery, robot hardware and device evidence were
not exercised or marked passing by this visual refresh.

## Subsequent Console reorganisation

The Console now has Overview, Runs, Robots and Apps destinations and separate
workspace, health and profile settings URLs. Run, robot and app detail pages
have permanent paths. Workspace IDs, tabs and list filters travel in the URL.
Overview and app details use execution and deployment snapshots. Run detail
and Overview use the same decision queue and authenticated response operation.
Robot history links to run detail. Preparation, start, restart and stop have
review steps. Displayed Python source is bounded to 128 KiB.

From apps/console, ran `npm test` (10 files, 28 tests) and `npm run build`
(TypeScript and Vite passed). The deep-link component test opens a cancelled
run directly on Output, checks its robot URL, then checks the ended App UI state.
With the Vite server described above, ran
`node /tmp/rf-console-refresh/routes.cjs remix`. Chromium verified the
Overview widths at 320, 390, 768 and 1440px, mobile navigation, opening a run,
reloading Output and returning through browser history. These browser checks
used fixture records. The running API at localhost:8080 returned HTTP 401 for
an anonymous GET /v1/organizations. Live signed-in robot operations and remote
UI traffic remain unverified. The execution-specific UI proxy is an outstanding
transport dependency described in docs/mission-ui-transport.md.

### Studio operational layout, 21 November 2026

Node v24.22.3, npm 11.19.1. In apps/console, `npm run build` passed and `npm test` passed 31 tests across 12 files. The timeline test covers overlapping clips and distinct occurrence/receipt timestamps. The resources test covers missing and stale readings.

A Chromium check with fixture-backed authentication and API responses exercised Overview at 320, 390, 768 and 1440 CSS pixels, verified the stacked/split rows and document width, and opened a run inspector from the full timeline. Local deployment used `./up.sh -d --no-deps console`. Authenticated physical-device acceptance remains open. The current robot-status contract supplies no battery or storage samples. Hosted app-interface access depends on the execution-scoped proxy.

### Overview, Apps and Timeline refinement

The Console build passed with `cd apps/console && npm run build`. `cd apps/console && npm test` passed 31 tests in 12 files. Overview was checked in Chromium at 320, 390, 768 and 1440 CSS pixels with fixture API responses. The deployed Console answered HTTP 200 at `/timeline`. Timeline now has its own route. Active Runs rows expose an execution-specific View UI panel. Hosted interface access remains unavailable until the documented proxy work is complete.
