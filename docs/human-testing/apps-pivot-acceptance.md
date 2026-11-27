# Ringfour Apps · human acceptance sweep

Most repeatable checks now have an [automated sweep](apps-automated-acceptance.md).
Run `npm run test:acceptance` for component checks, or use its documented full
and explicit live profiles. Reports distinguish real process tests, browser
fixtures, live workflow results and gates still needing operator/user evidence.

Use **one publisher, one consumer, and the same set of runs throughout**. Start with the everyday workflow (1–5). Then do the checks that interrupt a disposable robot (6). Section 7 lists checks that need a different environment. You can pause after any section.

Use **Pass**, **Fail** or **Pending — missing …** for each checkbox. If you already saw the exact result on this build, record the run or operation ID and move on. Your earlier Camera Snapshot Loop stop, photo download and Logbook checks are useful evidence. The new questions include whether the *other* robot keeps running and whether those same files survive uninstall.

## 1. Set up the two desks once

| | Publisher A | Consumer B |
|---|---|---|
| Unix shell | `cdr` | `ring1` |
| Browser | Your normal Studio session | Separate browser profile signed in with the Outlook-backed account |
| Workspace | Your configured official publisher, e.g. kraftwerk **if configured** | A workspace owned by ring1 that cdr cannot access |
| Device | A disposable, enrolled virtual robot | A different disposable, enrolled virtual robot |

- [*] Open Studio at <http://localhost:5173> in both profiles. In **each shell** run the commands below. Match the user, selected workspace (star) and robot to the table. Use `ringfour-cli org select UUID` if necessary. Use each user's installed CLI and keyring.

```sh
ringfour-cli whoami
ringfour-cli org list
ringfour-cli robot list
```

- [ ] Open **Robots → robot detail** in each workspace. Record its UUID, name, online state, inventory time, camera/IMU and engine target. Match each robot to its Daemon and network. Reuse healthy enrolled robots. If only virt2 is up, continue the single-robot checks and leave two-device rows pending. For numbered setup, enrollment and port conflicts use [AP-13 steps 1–2](AP-13.md#2-prepare-two-independent-app-capable-robots).
- [ ] Prepare **Hello World v1 and v2**, **Robot Check** with live settings and its summary file, **Camera Snapshot Loop** (stationary, 30 s), and the **human-decision fixture**. Use disposable releases/installations for removal and refusal tests. The publisher recipe is in [AP-13 steps 3 and 6](AP-13.md); the decision fixture is in [AP-10](AP-10.md#prepare-the-runtime-test-app). For listed publication use the configured official workspace and local registry. Check publisher entitlement, migrations and Daemon uninstall support using [AP-18 prerequisites](AP-18.md#before-starting).
- [ ] Keep a scratch note of **A/B workspace and robot IDs, App ID, v1/v2 revision IDs and image digests**. As you go, add installation ID, configuration revision, operation ID and run ID. Only IDs need to cross shells. Keep credentials, tokens and keyring contents out of the notes.

## 2. Publish, share and install (AP-05–07, 11–13, 18, 21)

| Done | Who and action | What you should see |
|---|---|---|
| ☐ | A publishes Hello World v1 as listed. B searches the Store, checks publisher and release, and imports it twice. | One official listing, the exact published digest and one import. Publishing/importing starts nothing. |
| ☐ | B publishes a separate test release with default visibility, then requests listed visibility from B's ordinary workspace. A searches for the private release. | Private by default, no unauthorized listing, and a server refusal for ordinary listed publication. |
| ☐ | B shares a private revision with A. First try its share ID in an unrelated test workspace, then import in A. Revoke it and try a fresh import with the revoked grant. | Only the named destination can import. Revocation prevents fresh use; the existing import survives. Use a second fresh share/revision for the revoked-before-import case if replay returns the existing import. [Share procedure](AP-05.md) |
| ☐ | B opens a listing and chooses Install. Inspect required/optional capabilities, change robot, cancel once, then install on B's compatible robot. Refresh during the operation. | Picker names the robot. Known incompatibility blocks install with a reason; stale/missing inventory reads unknown. Pending survives refresh. Open App appears for the confirmed installation. No run or workload is created. |
| ☐ | A installs the same App on A's robot. On B, install another App too. Try the same install request/key twice and then change the configuration under that key. | Separate installations per target/App; identical retry returns the same operation; conflicting input is refused. [Request examples](AP-07.md) |

For a **CLI-to-device** pass, use the CLI for at least one install, configure, start and stop. Verify each result in Studio. Below, replace uppercase placeholders with the saved IDs or paths. Use an idle Hello World installation and a JSON file containing `{"message":"Hello from ring1","repeat":20,"interval_ms":1000}`. For each mutation, wait on its returned operation ID before the next dependent step.

```sh
ringfour-cli app search "Hello World"
ringfour-cli app import REVISION_ID
ringfour-cli robot capabilities ROBOT_ID --revision REVISION_ID
ringfour-cli app install REVISION_ID --robot ROBOT_ID
ringfour-cli app operation OPERATION_ID --wait
ringfour-cli app installations --robot ROBOT_ID
ringfour-cli app configure INSTALLATION_ID --config CONFIG_FILE
ringfour-cli app start INSTALLATION_ID
ringfour-cli app run RUN_ID
ringfour-cli app logs RUN_ID
ringfour-cli app stop INSTALLATION_ID --run RUN_ID
```

## 3. Run and inspect real work (AP-08–13, 17, 19, 21)

- [ ] **One slot per robot.** Start a long-running photo App on each robot. Record both run IDs. While B is busy, try starting B's other App. Expect a conflict. Stop B by exact run ID; A continues. Start B again and retry the old stop; the newer run continues. If a robot name matches multiple devices in a workspace, verify the CLI requests an unambiguous target.
- [ ] **State agreement.** Follow B's exact run through Home → robot → App Overview → Activity → Logbook, with the CLI run record alongside. Watch one start and one stop without refreshing. Expect the same robot, installation, run ID and eventual outcome everywhere. Pending should be visible until confirmed. Note if the whole page flickers, loses focus or shows an older run as active. If retained, compare historical run `66ac710b-cec1-460d-91be-8675436c9d98` on these surfaces too.
- [ ] **IMU and startup values.** On an idle Robot Check, save a different allowed sample count. Try an out-of-range value and a stale edit from a second tab. Expect a refusal with the last good value intact. Start it normally and count the advancing IMU samples in stdout. Open its **own summary artifact** and check its contents. A photo from another App cannot close this particular check. Record the release and run ID if the summary is missing.
- [ ] **Live settings and fractions.** On a release declaring live settings, save a supported value while idle; start and check actual behavior. During a long enough run, submit another value, see pending then device-confirmed, and try a value the App rejects. Confirm the prior applied value survives rejection and refresh. Compare `ringfour-cli app settings show INSTALLATION_ID --json`. On a release declaring a number, save an allowed fraction (e.g. 2.5), reopen and verify it reaches the App. Check optional number clearing, integer bounds, false/enum values and any declared next-start environment mappings. A release without live settings explains that in Studio. [Settings cases](AP-17.md)
- [ ] **Files.** Keep the camera run alive for at least two photos. In Files filter by robot/run and path, apply the same filter twice, browse folders, try a no-match filter and load another page if available. Preview and download one photo. Compare its size/hash with its receipt (`sha256sum FILE`); download it via CLI too. Stop and reopen the run. Files stay readable, no new photos appear, and identical filenames in other runs remain distinguishable. An invalid receipt must never produce a verified preview. [CLI download recipe](AP-12.md#command-reference)
- [ ] **Logbook and decisions.** Select a Logbook group in the Runs card below the filters, apply the visible filters and click the run row. Inspect timestamps, actor, expanded events, separate stdout/stderr, pause/resume, copy/download and run-scoped Files. Open its exact installation and return to the filtered list. With the decision fixture, answer a prompt as an authorized member; check submitted → applied and responder attribution. On fresh runs test timeout, waiting without timeout, competing answers and stop while waiting. Expect one applied answer at most and visible cancellation/timeout where applicable. [Fixture steps](AP-10.md)

### Storefront and installation context

- [ ] Open Camera Snapshot Loop in Store. Check the image-led cards. Camera Snapshot Loop uses its simulator frame, and non-Store installation rows retain the camera icon. Select a different release and check that the description changes to that release’s text. Check the publisher credit, compatible robot, and **Open on** with that robot’s name. With only one robot, installation names that target directly.
- [ ] From a robot, open **Choose an App**. Verify its name in the Store selector. Open the featured listing, check the sample caption and camera requirement, and confirm **Install on** names your robot. Close during installation, reopen, and check the reported outcome. **Open on** should lead to that installation with the App stopped. Start it and compare your own first photo in Files with the Store sample.
- [ ] With several robots, open a listing from **All robots** and use **Choose robot**. Compare an eligible target with an incompatible or unreported target. Check that unavailable targets explain why confirmation is disabled. Use browser Back from a listing and verify the selected robot, search, scroll and focused card.
- [ ] Open the robot itself. Its header shows architecture, OS, App protocol, profile model and version, and the device ID. On a wide screen, Installed Apps and Capabilities start side by side, with Recent App runs beneath both. On a phone the sections stack.
- [ ] Use the Start control in the installed App header and open Overview. The latest verified photo should appear with a link to that installation’s Files tab. Switch installations and check the photo scope.
- [ ] Reopen a failed test run. Home should lead with the robot’s current state, then link its last outcome to the exact run. Needs attention should show unanswered decisions. Compare the robot name and event labels in Logbook, Timeline and Installation Activity.
- [ ] Open Profile, Organizations and System health from a robot detail. The page heading’s Back link should return to that robot within the same workspace.

## 4. Review, update, roll back, remove (AP-07, 12, 18, 21)

Use B's **test** installation. Save its v1 installed revision, configuration revision, an old run link and a verified file link first. Keep A's installation for comparison.

1. [ ] **Review v2 in Studio.** Inspect App, robot, current/target release, changed requirements and settings. While running, attempt an update and uninstall. Both must be refused. Stop and wait for confirmation.
2. [ ] **Update while idle.** Apply v2, refresh during the operation, then open the installation. The binding changes after device acknowledgement and no run starts. Run it once and check v2 behavior. The v1 run still shows its original configuration, digest and output.
3. [ ] **Rollback.** Stop v2 if needed. Select the saved v1 release/configuration pair and follow the operation. The old pair is restored, the robot stays idle and v2 history is readable. With a stale tab or competing operation, a conflicting change must be refused.
4. [ ] **Remove and reinstall.** Open uninstall, verify App/robot/release in the confirmation, cancel once, then confirm. After acknowledgement only B's installation disappears. Reopen its old run and download its file; A's installation still works. Reinstall on B and record the **new installation ID**. Check both CLI and Studio agree. [Exact update, rollback and removal procedures](AP-18.md)

## 5. Switch people and check Studio (AP-11–13, 17, 19, 21)

- [ ] **Foreign IDs.** As cdr, open B's robot, installation, run, file and decision links with B's workspace ID. Repeat a read from cdr's CLI, e.g. `ringfour-cli app run B_RUN_ID`. Expect a denial/unavailable result with no B data or preview. Do this before granting any shared access to B.
- [ ] **Role changes.** Invite ring1 to **A's test workspace** as Viewer and accept with the Outlook login. Verify read access and a refused import/configuration/start through CLI and Studio. Change the test member to Operator, then Developer: verify Operator can start/stop/respond but cannot change deployment settings; Developer can manage Apps but cannot perform owner-only administration. Test invitation and last-owner/self protection in the test workspace. Keep B private from cdr throughout.
- [ ] **Target selection.** The two-workspace test above gives each workspace one robot. For the selector check, use **two robots in one test workspace** with the same App installed. Open the App from the library and from each robot. Check explicit target, installed release, Overview/Settings/Files/Activity, refresh and deep links. Changing settings on one leaves the other unchanged. Mark this row pending if the only two robots belong to different workspaces.
- [ ] **Navigation and edits.** Edit a field without saving. Try changing tab, workspace, robot, browser Back/Forward and refreshing. Cancel discard and verify the same URL, selection and edits; then accept and verify navigation. Test filtered Back links, new-tab links, old Logbook aliases, unknown URLs and switching workspace without a flash of old data.
- [ ] **Robot onboarding and edge states.** In an empty test workspace, follow Store → Enroll a robot. Exercise expired pairing code, new and existing unregistered robot, return to listing and Device access. Find the Access section below Recent App runs and issue/rotate/revoke only disposable credentials. Check offline/last-seen, stale versus empty inventory, an incompatible release, imported-but-uninstalled Apps, current and last-run outcomes, errors and retry. [Studio scenarios](AP-21.md#home-and-robots)
- [ ] **Keyboard and small screen.** At desktop, tablet and phone widths visit Home, Robots, Apps, Store, Logbook, Organizations, Health and Profile. Use keyboard focus, the Organization dropdown in the top bar or the workspace picker under My profile, the drawer, dialogs and Escape. Check focus returns, long titles wrap, tables scroll internally, and controls remain reachable. On a signed-in App, switch Overview ↔ Settings and watch a run update. Record any visible pause with the route and timestamp. [Detailed Studio checklist](AP-21.md)
- [ ] **Identity and retired paths.** Confirm existing users/orgs and App history are still present. Check CLI help, Studio navigation and active services for retired Mission/Runner workflows. This reconciles the older unchecked tracker entry if you already signed it off. [Retirement checks](AP-14-15.md#8-retirement-and-sign-off)

## 6. One disposable recovery session (AP-07–10, 08V, 13, 17–18)

Run these after the everyday checks, while you control the disposable topology. Use the linked procedures for commands matched to the Compose project. Preserve enrollment, volumes and network. Record operation, run, container ID and StartedAt **before** interrupting anything.

| Done | Operator action | Pass condition |
|---|---|---|
| ☐ | Inspect the singleton and numbered topologies; replace Daemon A during a run while B is producing output. Repeat for B. Check safe-down on the disposable project. [Topology procedure](AP-08V.md) | Only Daemons have engine access. Networks, ownership labels and namespaces match. Each adopts its original container without relaunch, the other continues, a completed run stays terminal, and retained state survives shutdown. |
| ☐ | Disconnect a Daemon; submit a decision while disconnected, then reconnect. Interrupt the API after local output/file publication and restore it. [Runtime recovery](AP-10.md#recovery-and-negative-checks) | Offline is last-known; submitted stays provisional until applied once. Late output/events/files reach the original run once, with the original bytes. Engine uncertainty never fabricates a stop or decision. |
| ☐ | Interrupt acknowledgement of a live setting and replace the Daemon. On another run stop while an edit is pending and start again. [Settings recovery](AP-17.md#recovery-scenarios) | Same application ID reconciles once. No relaunch, unconfirmed value reported as applied, or old edit leaking into the new run. |
| ☐ | Attempt an incompatible update or stage a new uncached image while its disposable registry is unavailable. Restore service and retry. | Failure and reason remain visible; the prior installed release/config still runs. A valid retry completes. |
| ☐ | Submit removal while the disposable robot is offline, refresh, replace the API/Daemon with retained state, then reconnect. Reinstall and exercise delayed old removal in the isolated fixture. [Removal recovery](AP-18.md#uninstall-and-reinstall) | One pending operation completes once; retained evidence and the new installation survive. Capture fixture replay separately if precise delivery timing is unavailable live. |

## 7. Conditional gates and engineering follow-ups

| Gate | What needs human access or judgment |
|---|---|
| Hosted private registry (AP-05) | With the intended private provider configured, publish/import as distinct principals and verify authorized pull and denial without a grant. Local anonymous registry results cover the local deployment only. [AP-05](AP-05.md) |
| Hosted Studio (AP-21) | At the intended HTTPS origin, use real Auth0 login, callback, refresh, direct App link, session expiry/recovery and logout. Verify the same-origin API and that protected data is inaccessible after logout. Record the origin and build. |
| Second platform (AP-16, deferred) | When the second supported platform is available, run the **same immutable release** on it and the first platform. Record both observed targets, selected image digests, sensor results and artifacts. Two Base101 instances cover AP-13 only. |
| Performance and release identity (AP-19) | If signed-in navigation or actions still lag, record browser action time, matching operation/run and first visible confirmation. Check a metadata-only release rename with a reused image digest: new immutable manifest, correct Store name, old run still shows old release. The 10-robot/100-App load matrix, correlated SQL/Daemon timings and percentile calculations belong to engineering. [Measurement procedure](AP-19.md) |
| AP-17.F1 process rerun | Engineering reruns the corrected raw-JSON/nonfinite-value harness. Your fraction round trip in section 3 covers the live form. A missing Robot Check summary is also an engineering fix; repeat just that run after correction. |

## Sign-off

Copy one row per failed or pending checkbox, plus a passing run/operation reference for each completed section. Record both workspace roles and the deployed build once. A missing robot, entitlement, fixture or hosted environment leaves only its dependent checks pending.

| Section / check | Pass, Fail or Pending | Workspace / robot | Run, operation or file ID | Expected vs observed / missing prerequisite |
|---|---|---|---|---|
| | | | | |

Attach the results to the [AP-13 acceptance record](AP-13.md#completion-record) or the corresponding AP evidence. No database reset or repeat of accepted AP-01–04 work is needed for this sweep.
