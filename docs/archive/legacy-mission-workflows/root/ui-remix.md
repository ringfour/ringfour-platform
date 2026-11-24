ok, you know what, full spec, let's do it, I agree
Ringfour Console — information architecture and operational UX specification
Reorganise the console around apps, robots, and runs, with the run as the place where software execution, robot state, human decisions, and evidence come together.

Preserve the current visual identity. Use Overview and the existing Logbook detail as the design reference.

1. Product doctrine
Every operational view should answer:

What needs my attention? What is happening now? What can I do next?

Connect those answers to:

People: responsibility and actions.
Apps and runs: intended work and reported progress.
Robots: observed state and physical consequences.
Present known facts accurately. Distinguish:

Responsible person from the person who started or last acted on a run.
Robot connectivity from execution state and physical readiness.
Requested state from observed state.
Successful execution from a verified physical outcome.
Current information from stale or unavailable information.
Missing information should remain explicit.

2. Terminology and navigation
Concept	User-facing term
Reusable executable package	App
Published revision	App version
App/version configured on a robot	Deployment
One execution instance on a robot	Run
Events, decisions, and evidence within a run	Logbook
Primary navigation:

Overview · Runs · Robots · Apps

Bottom workspace/account area:

Workspace selector and workspace settings.
Members and invitations.
System health.
My profile and sign out.
Remove Deployments and Logbook as primary navigation destinations. Their capabilities move into Robot and Run detail respectively.

Apply the terminology throughout headings, buttons, empty states, notifications, and help text. Internal API or database names may remain unchanged.

3. Navigation and routes
Use addressable pages with browser back/forward support.

Route	Destination
/overview	Operational overview
/runs	Run list
/runs/:runId	Run detail
/robots	Robot list
/robots/:robotId	Robot detail
/apps	App library
/apps/:appId	App detail
/settings/workspace	Workspace administration
/settings/health	Platform health
/settings/profile	Personal profile
Tabs and list filters should be reflected in the URL. Preserve filters and scroll position when returning from a detail page.

Every reference to an app, robot, or run should link to its corresponding page when the user has access.

4. Overview
An operational handover ordered by attention, current work, and outcomes.

Header
Show:

Workspace.
“Overview”.
Compact operational summary.
Connection/freshness indicator and refresh action.
Example:

3 runs active · 1 decision awaiting you · 1 operation needs recovery

Counts link to filtered lists. Avoid a dominant row of lifetime inventory statistics.

Needs attention
Show actionable situations such as:

Pending human decisions.
Failed runs requiring investigation.
Desired/observed deployment mismatches.
Stale robot information affecting an active run.
Explicitly unassigned responsibility, once ownership is supported.
Each item includes:

Element	Content
Headline	What needs attention
Context	App, robot, and run
Human context	Assigned person, initiator, or last actor with an accurate label
State	Relevant execution and robot facts
Time	Waiting duration or last update
Primary action	Review decision, inspect run, investigate deployment
Group multiple symptoms from the same run where practical. Avoid several competing cards describing one underlying incident.

Order pending decisions by declared urgency and deadline where available; otherwise use age. Keep failures and stale-state issues distinguishable.

When empty, collapse to a compact “No outstanding requests or issues.”

Happening now
Show active runs, including queued, starting, running, waiting, and stopping states supported by the backend.

Each row shows:

App name and version.
Robot name and connectivity.
Run state.
Latest reported activity or progress.
Responsible person if explicitly assigned; otherwise accurately labelled initiator.
Freshness.
Open run and app UI shortcuts.
Do not invent progress percentages. Continuous apps can report activity, counts, elapsed time, or milestones.

Recent outcomes
Show a short list of recently finished runs:

App and robot.
Outcome.
Human context.
Completion time.
Reported result and evidence count, when available.
Use “Execution succeeded” when no richer outcome is supplied.

Robot summary
A compact secondary area showing robot connectivity and occupancy, with a link to Robots. Do not label a robot “ready” solely because it is online and idle.

5. Runs
Runs becomes the existing execution-history capability expanded into the main operational destination.

Run list
Default to Active. Provide:

Active · Needs attention · Finished · All

Filters:

App.
Robot.
Run state.
Started by.
Responsible person, when supported.
Time range.
Rows contain:

Column	Content
App	Name and version
Robot	Linked robot name
Situation	Run state and latest reported activity
People	Responsibility or accurately labelled initiator
Timing	Started, elapsed, or ended
Attention	Pending decision or relevant issue
Action	Open run; open app UI when available
Prioritise operational names over identifiers. Keep full IDs available through copy controls and technical details.

Use distinct treatments for failed, cancelled, succeeded, and unknown outcome. Cancellation is a neutral terminal outcome unless accompanied by a separate fault.

Run detail
A permanent page retained throughout and after execution.

Persistent header:

App name and version, linked.
Robot, linked.
Run state.
Start/end times and elapsed duration.
Responsible person, if known.
Initiator, separately labelled.
Relevant freshness.
Primary contextual action.
Tabs:

Overview · App UI · Logbook · Output

Show pending decisions prominently across the run page, with a link to review them.

Run Overview
Organise into:

Current situation: reported activity, progress, blocking condition, or final outcome.
Attention and actions: pending decisions and permitted controls.
Robot context: connectivity and relevant observed state, with timestamps.
Recent events: concise activity trail linking to Logbook.
Result and evidence summary: available artifacts and reported outcomes.
A run without structured progress can show its latest event and execution state.

App UI
The interface belongs to the run instance.

Use the app’s declared interface name when available.
Offer Open app UI ↗ from run headers, Overview rows, Robot detail, and App active-run lists.
For multiple active instances, each link identifies its robot and run.
Show endpoint availability: starting, available, unavailable, or run ended, according to actual endpoint/lifecycle information.
Do not infer reachability from robot connectivity.
Use an embedded interface only where supported by the existing endpoint, authentication, and framing setup. Always provide an external-open option. Embedded failure must leave the rest of the run page usable.

An ended run retains the App UI tab with an explanatory state; it must not silently open a replacement run’s interface.

Logbook
Carry forward the existing timeline and evidence gallery.

Present:

Lifecycle events.
App-authored events.
Human requests and responses.
Control actions and their observed results.
Associated evidence.
Human-decision entries should expose readable information first:

Prompt.
Request time.
Assignment, if supported.
Available choices.
Responding person and response time.
Selected response.
Subsequent reported execution events.
Keep raw payloads and technical metadata in a secondary disclosure.

Record a submitted decision separately from the robot’s acknowledgement or resulting state. Do not imply the action executed merely because the platform accepted it.

Output
Provide stdout and stderr with:

Readable monospace formatting.
Copy/download controls.
Clearly indicated streaming state.
Follow/pause behaviour for live output.
Bounded rendering for long logs.
Completed runs
Retain the same URL and page structure.

Replace live controls with outcome and evidence. Stop presenting historical content as live. Unknown execution outcomes must remain visibly unknown until reconciled.

6. Robots
Robot list
Show:

Name.
Connectivity and last seen.
Current app/run.
Relevant deployment state or mismatch.
Attention indicator.
Provide search and filters for connectivity, occupancy, app, and attention.

Selecting a robot opens its detail page. Avoid a blank detail pane as the default list experience.

Robot detail
Header:

Robot name.
Connectivity and last seen.
Current run summary.
Primary contextual action.
Tabs:

Activity · Deployment · Configuration

Activity
Show:

Current run with app/version, people, reported activity, and app UI shortcut.
Relevant robot observations.
Previous runs linking to their pages.
Selecting a previous run navigates to that run. Never append execution detail beneath the full history table.

Deployment
Move existing deployment information here.

Lead with:

Selected app and version.
Desired state.
Observed state and observation time.
Explanation of any mismatch.
Current or latest run.
Last operation and error, when present.
Available deployment/recovery actions.
Technical IDs and raw metadata belong in a disclosure.

A deployment can persist through several runs. A restart creates or leads to a new run according to existing backend semantics; it must not overwrite the previous record.

Configuration
Include existing robot-management capabilities:

Identity and organisation.
Robot profile.
Daemon and protocol versions.
Registration information.
Technical identifiers.
Management actions.
Keep consequential controls separate from routine configuration.

7. Apps
App library
A searchable list showing:

App name.
Description, when supplied.
Latest published version.
Publication time.
Active run count.
Deployment count, where available.
Opening an app defaults to its Overview.

App detail
Tabs:

Overview · Versions · Source

Header actions include Run app and any existing deployment action supported by the platform.

Overview
Show:

Description/readme.
Latest version.
Declared inputs, requirements, and interface information where available.
Active runs, each linked to its robot and run UI.
Deployment references.
Recent runs.
Versions
Show published versions and relevant metadata:

Version.
Published time and publisher, where available.
Entrypoint.
Python requirements.
Associated runs and deployments.
Keep hashes, lock metadata, and UUIDs secondary.

Source
Provide a file selector or tree and syntax-highlighted source.

Version selection must visibly control which source is displayed. Handle long lines and large files without expanding the entire page.

Run app flow
Collect the supported parameters:

App version.
Target robot.
Declared inputs, if supported.
Review of the requested operation.
Before submission show:

What app/version will execute.
Which robot is affected.
Existing activity or relevant conflicts.
What the backend action will do.
Use explicit action labels. After acceptance, open the resulting run or operation status; distinguish acceptance from execution starting.

8. Human decisions and controls
Use one shared decision presentation across Overview and Run detail.

Before a response, show the request, app, robot, available context/evidence, deadline, and declared consequences where supplied.

Do not invent domain meaning for choices. App authors define what “Continue”, “Skip”, or other options do.

After submission:

Prevent duplicate responses.
Show submission/acknowledgement state.
Handle expired or already answered requests.
Identify the responding person.
Link to the recorded decision and subsequent events.
Controls must be permission-aware. Explain unavailable controls when that explanation helps the user proceed.

Use confirmation or a review step for controls with meaningful consequences. Avoid confirmation dialogs for navigation and routine inspection.

9. Visual system and interaction rules
Preserve orange, dark surfaces, thin borders, and existing typography.

Orange identifies primary actions, selection, and emphasis.
Teal communicates successful or healthy observations.
Warning communicates attention or mismatch.
Danger communicates faults and destructive consequences.
Neutral styling communicates cancellation, inactivity, and historical metadata.
Use text and icons alongside colour.

Give priority to operational headlines and names. Keep timestamps, technical labels, and IDs quieter. Use human-readable dates, with exact timestamp and timezone available on inspection.

Constrain reading widths for forms and prose. Use wider layouts for operational lists and evidence.

Fix the image preview as a viewport-level dialog, outside card clipping containers. Support close, Escape, full image viewing, and download; provide previous/next navigation for image collections.

10. State, freshness, and implementation boundaries
Keep these as separate dimensions:

Dimension	Examples
Connectivity	Online, offline, last seen
Run lifecycle	Queued, running, succeeded, failed, cancelled
Reported activity	Inspecting, awaiting input, collecting samples
Deployment intent	Desired versus observed state
Interface availability	Available, starting, unavailable
Ownership	Assigned person, unassigned, unavailable
Use existing backend enums and capabilities. Do not introduce UI-only states that imply unsupported runtime guarantees.

Show stale data as last known information. When the console loses its connection, retain useful content with an explicit stale indicator and disable actions that cannot be safely submitted.

Preserve the selected row, open disclosure, scroll position, and user input during live updates.

Ownership, structured progress, urgency, action consequences, and interface metadata may require backend additions. Treat these as explicit capabilities:

Render them when supported.
Label missing information honestly.
Keep Started by separate from Responsible.
Do not create a universal assignment or progress model solely in the frontend.
11. Delivery order
Navigation and terminology: Apps, Robots, Runs; move settings.
Runs: migrate existing Logbook list/detail into permanent run pages.
Cross-links: connect apps, robots, runs, and app UI endpoints.
Robots and deployments: reorganise details and remove duplicate destination.
App library and detail: versions, source, active instances.
Overview: attention, current work, recent outcomes.
Polish: image preview, freshness, status styling, readable decisions, long-output handling.
Data extensions: explicit ownership and richer app-reported operational metadata.
12. Acceptance criteria
Every run has one permanent, directly addressable page.
Active and completed runs are accessible from Runs.
App and Robot pages link to their relevant runs.
Every app UI shortcut resolves to a specific run.
Human responses and resulting observed events remain distinguishable.
Deployment intent and observed execution remain distinguishable.
Historical records do not display misleading live indicators.
Run selection never hides detail below a long table.
Cancellation is visually distinct from failure.
Missing ownership or progress is never fabricated.
Image previews display outside gallery-card boundaries.
Browser history, deep links, permissions, and live updates work without losing user context.
Existing execution history, artifacts, decisions, and deployment capabilities remain accessible after migration.
