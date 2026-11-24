# Legacy mission workflow archive

These documents describe the earlier mission/source-upload model: mission
publication and deployment, fixed Runner execution, mutable development
sessions, mission-authored Logbook/HITL, and mission-hosted Gradio interfaces.
They are retained as historical context and for AP-14/AP-15 migration and
regression work. They do not describe the current Apps product workflow.

Use the active [Apps pivot plan](../../milestones/apps-pivot.md) and its
[current human acceptance guides](../../human-testing/AP-08-10.md) for current
behavior. Legacy runtime or persistence references should be read only as the
compatibility boundary needed until their remaining callers are retired.

## Contents

- `milestones/`, `evidence/`, and `human-testing/`: implementation and acceptance
  history for the earlier numbered milestones.
- `contracts/`: contracts for mission, script, Runner, development-session,
  mission Logbook/HITL and UI flows.
- `planning/`: early acceptance, demo planning, development setup, transfer
  design, and a historical handoff.
- `applications/`: the former CLI, Runner, runtime, Console and Gradio walkthroughs.
- `infra/`: the superseded interim Runner-plus-App Daemon Compose overlay.
- `root/`: historical progress, design exploration, UI specification and
  repository review notes.
