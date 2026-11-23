# Verify the Console visual refresh

Use the existing development Console setup in `apps/console/README.md`, with a
real configured Auth0 application and Platform API. Run `npm run dev` from
`apps/console`, open its configured origin and sign in normally. Production
builds continue to show the development-only restriction.

1. At desktop width, inspect Overview. Headings should be smaller, panel edges
   square, labels readable and inventory counts compact. Orange marks actions
   and selected navigation; teal marks observed connectivity/success. Online
   status does not imply physical readiness.
2. Inspect the sidebar. Organizations, System health and My profile are grouped
   under Settings at the bottom. Open each and verify the contextual header
   matches the destination. Existing capabilities and permission checks remain.
3. Resize to 390px, then 320px. The brand and Menu remain visible. Open Menu,
   choose a destination, and verify the menu closes. Reopen it and use Close
   menu. The overview should fit the screen; swipe a wide robot table sideways
   within its panel to reach its remaining columns.
4. On Overview with no operator requests, the queue collapses to a compact
   message. With a genuine pending request, verify its prompt and choices remain
   visible. A submitted response must still say it awaits device confirmation.
5. Open Fleet, Missions and Deployments. Check selected records, version/source
   controls and forms at desktop and mobile widths. Fields use 4px radii and
   actions use 6px radii. Use keyboard Tab to confirm visible focus indicators.
6. Open Logbook and compare failed, cancelled and succeeded executions where
   available. Failed is red, cancelled is neutral and succeeded is teal; text
   labels accompany every state. Filter labels and metadata remain readable.
7. Open an execution containing an available image, then Preview. The dialog
   must cover the viewport without clipping to its card. Close has initial
   keyboard focus; Tab and Shift+Tab stay within the dialog. Background scrolling
   is locked. Verify the image and Download image action are available. Close
   with Escape, then reopen and close with the close button. Focus returns to
   the opener and background scrolling resumes.
8. Enable the operating system's reduced-motion preference. Repeat navigation
   and preview interactions; motion should be suppressed without hiding content.

Use existing data and permissions for consequential controls. A visual check
does not require starting a new robot run or answering a live human decision.
The broader Apps/Robots/Runs route and page migration in `ui-remix.md` is separate
from this visual refresh.

## Verify the reorganised Console

Use Overview, Runs, Robots and Apps in the primary navigation. Use the
workspace selector in Settings to switch organisations. On Overview compare
the decision queue, active work and recent outcomes against known runs.

In Runs, choose Finished, filter by robot and open a run. Copy its URL, reload
it, then open Logbook and Output. Check evidence, keyboard navigation, image
collection controls and output copy/download. Browser Back should restore the
previous tab and filtered list. On an active run pause and resume output
updates. A terminal run should display recorded evidence and an ended App UI
state. Open the linked robot and app from the run header.

In Robots, search by name or filter by connection and occupancy. Open Activity,
Deployment and Configuration. A run in Activity should open the same permanent
run page as Runs. Check the requested and observed deployment states. Inspect
the review text before confirming any stop or restart.

In Apps, select an app, inspect its deployments and runs, choose a version,
then open Source and change files. Check that the selected version controls
the displayed source. On a designated test robot, review preparation and start
requests. Once a request is accepted, inspect the resulting run before treating
the app as running. On a second workspace verify all lists and links use that
workspace's data.

### Studio operational layout, 21 November 2026

1. Open Overview in a workspace with both idle and active machines. Inspect a pending request, then follow the robot, app, and run links in the register. At phone width the machine appears directly above its work. Check an offline machine for last-seen time and last-known work.
2. Open the recent timeline and inspect a clip. Open the full timeline, change the window, pan, and select another clip. Inspect a human event and compare occurrence and receipt times. Enable Live follow to return to the present.
3. Open Runs directly. Finished executions appear by default. Use Filter runs, switch between List and Timeline, and follow a run to its situation, logbook, evidence and output.
4. Open a robot from Robots, inspect deployment and execution together, then return to the register. Open an app from Apps, choose a published version and inspect its source. Review the launch scope before issuing a request intended for your robot.

5. Open Timeline from the global navigation and verify a robot track, time controls and run inspector. Return to Runs. Open View UI on an active row and confirm the panel names that run and robot and links to its interface details. Finished rows have no active interface control. Inspect Apps and confirm each row has a title, supporting activity, version and right-side Details action.
