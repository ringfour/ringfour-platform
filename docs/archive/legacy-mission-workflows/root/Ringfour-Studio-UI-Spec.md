# Ringfour Studio — operational UI specification

Date: 21 November 2026  
Status: implementation brief following product discussion  
Scope: console UI and interaction design; backend capabilities may be developed separately.

## 1. Direction and precedence

This document supersedes the earlier blanket use of tabs, persistent list sidebars, an Active-only Runs default, and the removal of robots from Overview.

Studio is the operating environment for robot applications. Its default user is an operator checking machines, following work, responding to requests, and understanding outcomes. Developers need deeper diagnosis and app inspection. Managers need an understandable account of current work and outcomes. Support these through progressive detail rather than three separate products or role-specific dashboards in this iteration.

The central questions are:

> What needs my attention? What is happening now? What can I do next?

Keep people, work, and machinery connected. The robot is the everyday point of recognition; its app and current run explain what it is doing. The app defines the meaning of work, progress, choices, and outcomes. Studio presents those reports and provides execution, interaction, and history.

The Studio landing-page promises are the acceptance lens:

- Know what's running.
- Keep people in the loop.
- Understand what happened.

Never present status without relevant context, progress without visible human context where known, or consequential actions without explaining their scope. Do not infer ownership from initiation, physical safety from connectivity, or verified task completion from process exit.

## 2. Users and boundaries

| User | Primary need | UI emphasis |
| --- | --- | --- |
| Operator — default | Check robots, run apps, follow work, respond to requests | Robot/work relationship, attention, progress, app interface |
| Developer | Test apps and correct behaviour | Versions, source, events, output, precise timing |
| Manager | Understand how work is going | Current work, exceptions, recent outcomes, history |

Vendors and integrators may use the same workspace to demonstrate, commission, and support installations. Workspace identity must be visible and switching straightforward. Do not build a reseller portal, billing-transfer flow, project-management system, or multi-customer control room in this scope.

Business exploration: Ringfour could own and rent robots together with programmability and an operator interface. This could bring the service closer to end users, but requires commercial validation and changes operational responsibilities. Record it as a hypothesis, not an assumption behind tenancy, navigation, billing, or UI labels.

## 3. Object model and language

| Object | Meaning |
| --- | --- |
| Robot | A registered machine, its observations, resources, and operational context |
| App | Reusable executable software |
| App version | A published revision of an app |
| Deployment | The app/version and requested state configured for a robot |
| Run | One execution instance of an app on a robot |
| Logbook | Events, human decisions, and evidence belonging to a run |

A robot persists across runs. An app version can have many runs on different robots. A deployment can persist across multiple runs. A restart must not overwrite the historical run. Stopping a run does not automatically imply removing its deployment.

Use Apps, Runs, and Robots consistently in user-facing language. A domain author may still call their workflow a mission. Internal API names need not be renamed for this UI change. Unversioned/direct executions remain supported and clearly labelled.

## 4. Navigation and page structure

Primary navigation: **Overview · Robots · Runs · Apps**.

Keep workspace selection and account/settings controls in the global shell. Workspace settings contain members/invitations and existing administration. System health and personal profile remain secondary destinations. Surface service degradation where relevant without promoting platform diagnostics to the operator's main task.

Use real addressable routes for lists and details. Preserve browser history, selected workspace, filters, time range, and list position when returning.

| Route | Purpose |
| --- | --- |
| `/overview` | Attention, robot/work register, recent activity |
| `/robots` | Scan and find machines |
| `/robots/:id` | Machine health and current work |
| `/runs` | Active work and execution history; List/Timeline views |
| `/runs/:id` | One execution and its complete record |
| `/apps` | Reusable app library |
| `/apps/:id` | App description, versions, source, deployments and runs |

### Remove the hybrid list/detail pattern

Opening a robot or app replaces the list with its full detail page. Do not convert the old table, filters, and workspace selector into a cramped permanent second sidebar. Keep the global shell. Supply Back to robots/apps/runs, informative breadcrumbs, and optionally a compact searchable entity switcher for occasional developer switching.

Relationships must remain visible through linked labels, not through retention of unrelated tables. Every run header identifies its robot and app/version. Every robot shows current work. Every app links to its deployments and runs. Entity names are explicit links; avoid making a whole complex row one ambiguous click target when it contains several destinations.

## 5. Visual hierarchy and colour

Keep Ringfour's Space Grotesk, orange, graphite, bone, thin rules, and machinery-inspired precision. Add hierarchy through composition, density, typography, and selective colour rather than giving every block an identical panel.

Suggested initial entity accents, to validate visually:

| Meaning | Accent |
| --- | --- |
| Brand, primary action, attention emphasis | Orange `#E47A00` |
| Robot identity | Blue `#527F9B` |
| App identity | Violet `#9568F5` |
| Run identity | Orange, supported by run icon and text |
| Healthy/succeeded | Teal `#36B5A0` |
| Warning | Accessible amber |
| Failure/destructive consequence | Accessible red |
| Cancelled, idle, unknown metadata | Neutral, with explicit wording |

Use entity colours in icons, linked chips, small rails, and contextual labels across pages. Status colours are a separate system. An app can have violet identity and a red failed status. Do not colour entire pages by entity or use decorative red/green identity colours.

Use black/white surface alternation deliberately: a dark global shell, light robot identity cells or selected summaries, dark work cells, and quieter history surfaces. Avoid large alternating slabs that make every section equally dominant. Validate all contrast; palette references are not permission to use low-contrast text.

Names and situations outrank metadata. Compact bars suit battery/storage; full charts require a useful time relationship. Forms should not span the entire screen. Keep lists genuinely tabular with aligned headings and readable row groups, taking cues from the Organisations member table.

## 6. Time language — global rule

Meaningful timestamps expose both absolute and relative time:

> 21 Nov 2026, 10:15:32 · 25 min ago

In dense rows, use date/time on one line and relative age beneath. Exact seconds and timezone can be revealed in a tooltip/detail when the compact form omits them. Event inspection always includes the exact timestamp and timezone. Distinguish elapsed duration from age: Started 25 min ago, Ran for 42 min, Waiting for 4 min.

Use the viewer's configured timezone consistently; show it on timeline controls and exact-time details. Relative ages update without moving focus or shifting layout. Distinguish event occurrence time from platform receipt time where both exist. Do not silently substitute receipt time for occurrence time.

Every observation has freshness. An offline robot's last-known resources remain inspectable with their age, not green-looking live readings. A last-seen time is not a task-completion time.

## 7. Overview — the operator's starting point

Order: compact summary, attention, robot/work register, recent timeline, concise outcomes if they add information. Devices must remain visible even with no active runs.

### Header

Show workspace context, Overview, last refresh, and a brief factual summary, for example: 3 robots connected · 2 apps running · 1 request awaiting you. Counts link to relevant content. Avoid repeated workspace selection and large lifetime totals.

### Needs attention

Use compact incident/request rows inspired by Infradeck: severity/type, situation, explanation, age, clear next action. A high-priority row can use a strong orange surface; lower-priority rows stay quieter. No oversized section nested around oversized cards.

Include pending human requests, meaningful current failures, deployment mismatches, relevant stale state, and resource warnings. Group symptoms of the same known situation. Historical failures do not remain incidents merely because they ended in failure. Do not claim an incident-resolution workflow exists unless supported.

Each item identifies robot, app/run, human context where available, reason for attention, and what its action opens or affects. Decision required and technical fault must remain distinguishable. No-attention state is one quiet line, not a large empty card.

### Split robot/work register — main surface

One aligned split row per robot. Shared boundary, clear internal division, explicit relationship: **Robot → running app / current run**. This is the everyday operational anchor, not separate duplicated robot and active-run sections.

| Robot side — machinery | Work side — current operation |
| --- | --- |
| Recognisable name, optional compact robot image/profile icon | App name and version, linked |
| Connectivity and last seen | Actual run state, linked run |
| Battery/charging, storage free, concise relevant resources | Reported activity and optional progress |
| Resource warnings and observation age | Initiator/responsible person, accurately labelled |
| Open robot | Started absolute/relative, elapsed duration, app UI, pending request |

Use a light identity/resource half against a dark work half as the first visual prototype. Retain alignment across rows, comfortable density, and clear entity accents. The work area can be wider. On narrow screens stack both halves within one row-card and retain the relationship label.

Running, deployed-but-not-running, idle, offline, and unknown situations must be distinct. A configured app is not necessarily executing. Idle robots remain visible with No app running and an optional last-run link. Offline robots show last-known work as last known. Preserve distinctions between app failure and robot disconnection.

Battery can be charging, discharging, externally powered/not applicable, unknown, or stale. Do not show 0% for missing data. Storage shows free/total plus a compact bar. CPU, memory, and temperature can be secondary indicators, escalating when relevant. Backend work is not a blocker for designing these states; use explicit fixture/demo data during development, never fabricated production readings.

### Recent timeline preview

Include a compact robot-track timeline below the register: recent hours, shared time axis, current Now marker, app-run clips, and important event markers. Label the window and timezone. Allow a small set of visible tracks with a count/link for the rest.

Click a clip to inspect the run; click an event to see a compact event detail. Open full timeline carries the window and robot selection into Runs. It is a preview of the same component/model, not a second unrelated chart. Prioritise requests, responses, failures, and evidence over dense debug events. Do not add terminal output to Overview.

### Recent outcomes

Use a short, quiet list only if outcomes are not adequately conveyed by the preview. Include app, robot, end time plus relative age, outcome, and reported result/evidence. Three recent items are sufficient by default; link to full history. Avoid giving history more area than current machines and work.

## 8. Runs — work and history together

Runs opens with useful content, not an empty Active-only filter. Show a compact Active now area when runs exist, followed by history with all runs included by default. Active work can be visually extracted without hiding history. Empty active state is shallow.

Provide **List / Timeline** view selection. Keep search, date/time range, robot, app, person, and status filters compact; use additional-filter disclosure where needed. Counts make applied scope explicit. Do not display a second workspace selector or a huge filter form before the content. Needs attention is a filter, not a confusing mutually exclusive lifecycle category.

List rows align app/version, robot, activity/outcome, human context, start/end, duration, and status. Display names or You precede IDs. Distinguish cancelled from failed and unknown outcome from confirmed failure. Preserve direct/unversioned runs.

### Full timeline — robot tracks, app-run clips

This is a first-class Runs view. One horizontal track per robot; app-run executions are clips on that track. A shared time ruler shows simultaneous work and successive runs across machines.

- Track label stays visible while panning and links to the robot.
- Each clip labels app and version where width permits; duration derives from start/end or start/Now.
- Active clips extend to Now and have a clear live treatment.
- Finished clips retain outcome labels and an outcome edge/icon; use app identity accents without confusing them with status.
- Markers represent recorded lifecycle events, human requests/responses, evidence, and faults.
- A bounded waiting interval is drawn only when recorded boundaries support it. A request alone does not prove the entire app paused.
- Disconnection can be a separate robot-state overlay when historical observations support it; do not infer motion or app state from connectivity.
- Unknown ending/outcome uses an explicit uncertain end treatment, not a false precise end.
- Preserve sub-lanes when overlapping executions are reported; flag inconsistency where relevant rather than hiding data to satisfy the one-active-run assumption.

Controls: Today, useful recent-window presets, custom range, zoom in/out, pan, Jump to now, and Live follow. Default to a recent working window; no shift scheduling model is required. User pan/selection pauses automatic follow so the view does not jump away. Zoom preserves the selected/central time.

At broad zoom, cluster dense markers and show counts. At close zoom, reveal individual events. Short runs retain a minimum selectable target without misrepresenting their actual time range. Multiple clips and requests must remain distinguishable. Virtualise large track/event sets and fetch/render by window.

Selecting a clip opens a contextual inspector with app/version, robot, people, start/end absolute and relative timing, duration, state, important events, and Open run. Selecting a marker opens exact event details, actor, evidence, occurrence/receipt times, and run link. The selected-time line is an inspection cursor, not playback of robot behaviour. Do not imply simulation, replay, or command execution.

Keyboard users can traverse tracks, clips, and events, open/close inspectors, and operate zoom. Supply a chronological event/list equivalent. Colour must never be the only encoding.

## 9. Run detail — one coherent operational page

A run has one permanent page and URL from start to historical record. Default content combines situation, interaction, recent events, and evidence. Do not put a mostly empty Overview behind a large metadata grid while hiding the explanation in Logbook.

Header: app/version and robot as linked, visually distinct identities; run state; concise timing; app UI action if available. Initiator and explicit owner are separate labels. Full execution ID, raw payloads, receipt metadata, and runtime implementation details belong in Technical details.

Main content:

1. Current activity or outcome, with actual explanation and progress if reported.
2. Human requests, relevant controls, and current robot context.
3. Run-duration strip with important events, using the timeline model.
4. Logbook timeline containing events and decisions in context.
5. Evidence gallery and reported results.

On desktop use a useful two-column arrangement for situation/interaction and events/evidence where appropriate. Do not force every subsection into identical giant cards. Output can remain a secondary tab, drawer, or expandable diagnostic panel because it is substantial developer content. App UI can be opened externally and optionally embedded in a dedicated panel; essential operational context remains immediately available.

Completed runs retain evidence and history. Hide unavailable live controls, explain ended interfaces, and stop misleading Live badges. A succeeded process is labelled Execution succeeded unless the app reports a richer verified outcome. Submitted human decisions and subsequent acknowledgements/results are separate events.

## 10. Robot list and detail

Robot list uses aligned readable rows derived from the Overview split register, with search and compact filters. Show identity, connection, current work, battery, storage, and attention without drilling in. Optional density/view choice may come later; one excellent row layout comes first.

Robot detail defaults to a combined operational summary:

- Identity/profile, connectivity and freshness.
- Resource strip: battery/charging, storage, CPU/memory/temperature where reported.
- Current app/version, deployment intent versus actual execution, run progress and people.
- App UI, decision access, and clearly scoped controls.
- Recent robot-track timeline with successive app-run clips.
- Recent runs with readable app names and dual timing.

Deployment state and current work should be understandable on this page without first selecting another tab. Put configuration, daemon/protocol versions, identity metadata, and uncommon management operations in a secondary Configuration area. Deployment history/advanced settings can expand from the visible deployment summary. Avoid Activity/Deployment tabs that split one operational story.

## 11. Apps

Apps remains a library for reusable software. Show name, description, latest version, active/deployed counts, and publication timing. Do not lead with hashes or a large search/filter stack. Opening an app is a full page; no persistent old library sidebar.

Tabs remain appropriate here: **Overview · Versions · Source**, because they are genuinely different substantial tasks.

Overview: purpose/readme, declared interface/inputs where supplied, deployments by robot, active runs, recent outcomes, and Run on robot action. Avoid three huge empty operational panels before the launch controls. Version selection clearly scopes source and launch while historical runs show their own actual versions.

Versions shows revision, publisher where known, publication absolute/relative time, and run/deployment links. Source has a readable file tree/selector and syntax highlighting with controlled overflow. Hashes, entrypoint, lock and Python metadata remain inspectable but secondary.

Run on robot flow identifies app/version, robot, supplied inputs, and conflicts. Explain whether the action starts a one-off run or changes a persistent deployment according to actual API semantics. Acceptance is distinct from running. Link to the resulting run/operation.

## 12. App interfaces and human decisions

The interface endpoint belongs to a specific run. Show Open app UI, or the app-declared interface name, beside current work in Overview, Robot detail, and Run detail. App pages list endpoints per active robot/run rather than one ambiguous global link. Gradio is an implementation detail, not the default interface label.

Availability states include starting, available, unavailable, ended, or unknown according to actual endpoint information. Connectivity alone does not prove reachability. External opening must work even if embedding is unsupported; never silently open a later run's interface from a historical record.

Use one shared human-request component. Show prompt, originating app/run/robot, declared choices, time/age/deadline, person/assignment where supported, evidence, and declared consequences. App authors define semantics; Studio must not invent what Continue, Leave, Skip, or Cancel physically does.

After submission show pending acknowledgement, answered, expired, or conflict states as supported. Prevent accidental duplicate responses. Record actor and time; link to subsequent results. Permission-aware controls explain scope and use review/confirmation proportionate to consequences. Read-only inspections need no confirmation.

## 13. Data and loading contract

Design all requested UI states now; backend limitations are tracked separately. Create explicit fixtures for populated, idle, offline, low-battery, storage-warning, pending-decision, long-running, unknown-outcome, and history scenarios. Demo values must be visibly identified in demos and must not enter production fallback paths.

Keep connectivity, run lifecycle, reported activity, deployment intent, endpoint availability, resources, and ownership independent. Missing is unknown, not zero/healthy/unassigned. Explicitly unassigned differs from ownership unavailable. Historical resources require historical samples; current readings must not be shown as readings at a past event.

Preserve focus, input, scroll, selection, and open evidence during live updates. Show loading per region, useful empty states, permission failures, partial-data failures, stale connection banners, and retry. An error fetching one panel must not erase the whole operation.

## 14. Evidence and diagnostics

Evidence gallery supports images and files with readable titles, capture times where known, event association, preview, and download. Full preview renders outside card clipping containers as an accessible viewport-level dialog, with Escape, focus return, and previous/next images.

Output supports stdout/stderr, bounded rendering, follow/pause, copy/download, and clear historical versus streaming state. Raw JSON is a technical disclosure after readable event/decision content. No unbounded logs on the operator's starting page.

## 15. Implementation sequence

1. Establish shared entity links, timestamp component, status semantics, resource states, and populated fixtures.
2. Remove hybrid persistent list/detail sidebars; preserve deep links and return state.
3. Build the robot/work split row and use it on Overview and Robots.
4. Recompose Overview attention and outcomes; keep current machinery visible.
5. Build shared timeline primitives and full Runs List/Timeline views; add Overview preview.
6. Recompose Robot and Run detail around work, health, interaction, and events; reduce unnecessary tabs.
7. Refine App library/detail, source, launch and endpoint access.
8. Validate responsive behaviour, accessibility, long histories, live updates, and permission states.

## 16. Acceptance scenarios

- Operator opens Overview and sees connected robots even when none has an active run.
- Each split row makes which robot is running which app immediately clear.
- A deployed app with a failed run is not presented as currently running.
- Battery and free storage are visible in Overview/list previews and Robot detail, with unknown/stale states.
- One pending decision clearly outranks routine information and leads to its app/run/robot context.
- No active runs does not hide execution history or produce a page dominated by filters.
- Opening an app/robot removes the previous list instead of retaining it as a cramped sidebar.
- Run explanation, requests, timeline, and evidence are accessible together without unnecessary tab navigation.
- Timeline uses robot tracks and app-run clips with one shared time axis.
- Overview exposes a compact useful preview and carries its window into full timeline.
- Events expose date/time plus relative age; duration and age remain distinct.
- Request submission and resulting robot/runtime observations are not conflated.
- A missing end, stale resource sample, or unknown outcome is represented honestly.
- Idle/offline robots, direct runs, dense events, long-running clips, and empty workspaces remain understandable.
- Names and readable situations precede UUIDs, hashes, protocol versions, and raw payloads.
- Cancelled and failed have distinct appearance and wording; entity colours do not override status meanings.
- All significant interactions work by keyboard and on narrow screens.

## 17. Reference lessons

- Existing Organisations table: recognisable identities, aligned columns, readable actions; tables can work well.
- balena fleet/device views: contextual ancestry, combined device resources and work, lightweight list controls. Avoid copying its metadata dominance or ambiguous freshness.
- Infradeck overview: compact attention rows with severity, specific explanation, age, and action; selective strong colour.
- Studio landing page: know what's running, keep people involved, understand the run. The operational console must substantiate these promises.

This iteration improves the interface around existing product concepts. It does not introduce task queues, scheduling, physical playback, universal domain semantics, OS fleet management, or a business-model commitment.
