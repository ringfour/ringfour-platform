# Console navigation and card consistency — 26 November 2026

Final requested presentation (sidebar restored at the owner's request):

- Main navigation is back in the 72px icon sidebar, with the logo, hover/focus
  labels, active-page feedback and mobile drawer. Contextual back links,
  top bar contains the organization dropdown and account links. Back links
  live in page headings and feature tabs render inline within their pages.
- Logbook groups stay inside the Runs card after its heading, below filters.
  Their URLs, filtering and return context are preserved.
- App Store collection headers use the shared minimum heading height and
  remove the filter field's extra margins. Installed Apps use the Store's
  card/grid classes and artwork, while keeping robot and exact installation
  binding, running/idle state and installed-release descriptions.

Checks in `apps/console` (React 19.2, TypeScript 5.9.3, Vitest 4.1.12):

```
npm run typecheck
npm test -- src/views/StudioShell.test.tsx src/features/logbook/AppLogbookPage.test.tsx src/features/apps/AppStorePage.test.tsx src/App.test.tsx src/components/StudioManagement.test.tsx
node --check scripts/screenshot-demo.mjs
git diff --check
```

The initial pass had 32 passing cases. After restoring the sidebar, TypeScript
and 24 cases across StudioShell, AppLogbookPage, AppStorePage and App passed.
After removing the top bar, TypeScript and another 24 cases across StudioShell,
AppLogbookPage, App and StudioManagement passed. Tests cover sidebar ownership,
drawer focus restoration/account links, workspace-bound links, footer selection,
Timeline selection, page-heading back links and Logbook group/filter placement. Biome
checked all eleven changed implementation/test/style files without findings.

Latest follow-up restores the top bar and routes its Organization dropdown
through `useWorkspace.changeWorkspace`. TypeScript, Biome and 25 cases across
StudioShell, AppLogbookPage, App and StudioManagement passed. The new App-level
case verifies that cancelled navigation preserves the old organization and
selection, while an accepted change navigates from robot detail to the new
organization's robot collection. The saturated orange logo corner is preserved.
The screenshot fixture checks the restored sidebar and inline Logbook groups.

Browser visual acceptance remains pending: this environment lacks Chromium's
required system library and cannot launch it. No Platform or virtual stack
reload/build was performed. The operator must reload the Console, then check
desktop sidebar and mobile drawer, App Store header sizing, installed App cards
and Logbook groups against the requested layout.
