# Mission UI transport design

Status: implementation design for Milestone 10b. This does not claim remote UI
support is implemented or accepted.

## Boundaries

- A UI belongs to one active execution. Every request resolves its execution,
  robot and organization from Platform records; the browser cannot choose a
  robot, host or port.
- Mission code receives no Platform access token, robot credential, Runner
  token or UI gateway credential.
- The Runner owns the UI endpoint. It assigns an execution-scoped loopback
  port and reports readiness only while that exact execution is running. The
  browser gateway never accepts a URL or port supplied by mission code.
- `ringfour-ui.launch()` keeps its loopback, no-sharing and no-history defaults.
  It now reads optional `RINGFOUR_UI_PORT` and `RINGFOUR_UI_ROOT_PATH`
  environment variables, while explicit Gradio arguments take precedence.
  The Runner does not assign these values yet. An ordinary Gradio app without
  Ringfour UI support does not become remotely reachable by default.
- The current `127.0.0.1:7860` Docker port mapping is only a local acceptance
  aid. Hosted access must go through the authenticated Platform gateway.

## Request path

1. The Runner starts the mission with its configured private UI port and
   execution ID. It passes `RINGFOUR_UI_PORT` to mission code and exposes an
   authenticated execution status route; the route probes HTTP on loopback and
   returns `starting`, `ready`, `unavailable` or `ended`. A failed probe never
   makes an endpoint available. This port assignment and local readiness
   probe are implemented; gateway root-path assignment and remote routing are
   still open.
2. The authenticated Runner `/v1/ui` route reports a bounded list of active
   execution UI states to the daemon. The daemon probes it while connected and
   associates each execution with its enrolled robot. Runner status listing,
   daemon reporting/reconnect and Platform handshake/status reception are now
   implemented; HTTP request/response streaming is still open.
3. The daemon opens a **separate authenticated UI WebSocket** to Platform.
   Do not add browser data frames to the structured command/operation socket.
   The UI channel is designed to be multiplexed by execution ID and request ID,
   carrying bounded request/response headers and body chunks, cancellation
   and status frames. Version 1 carries HTTP request/response bytes, including SSE, but
   does not tunnel inner WebSocket upgrades: the verified Gradio 6.32.0 page
   uses HTTP/SSE and opened no WebSockets. Add inner WebSocket forwarding only
   when a supported mission UI demonstrates that need.
4. Console asks Platform to create a short-lived UI session for the selected
   execution using the signed-in user's bearer token. Platform checks that the
   execution is running and applies the existing Operator access boundary,
   then binds the session to that user, organization, robot and execution.
   Console embeds only that execution's isolated mission-UI origin; it never
   receives a Runner address or device credential. Use a distinct origin per
   execution so one mission's JavaScript cannot access another mission's UI
   on the same origin. A short-lived, single-use bootstrap is handed to that
   exact origin through strict-origin `postMessage`, then exchanged for a
   host-only HttpOnly session cookie. Do not put bootstrap or session
   credentials in the iframe URL. The authenticated session-creation endpoint,
   isolated bootstrap page and one-use cookie exchange are implemented; the
   direct API handshake was live-checked after migration. The Console entry
   point and cookie-authenticated UI proxy are not implemented.
5. The execution-specific UI origin proxies Gradio assets and traffic through the
   device UI channel to the Runner's reserved loopback endpoint. Recheck the
   session and execution state for new requests. Terminate active streams when
   the session is revoked, the robot disconnects, or the execution ends.

## Session and transport requirements

The proposed WebSocket is authenticated with the daemon's enrolled robot
credential before upgrade and is a different endpoint from `/v1/device/connect`.
Identity comes from that verified credential; `execution_id` on each request
must be checked against the authenticated robot and a currently running
execution. The connection has independent, strictly increasing message
sequences in each direction. The daemon begins with `hello`; Platform answers
with `welcome`. `heartbeat` keeps the tunnel alive. `ui_status` reports
execution-scoped availability. Platform sends `request_start`, zero or more
`request_chunk` frames, then `request_end`; the daemon replies with
`response_start`, zero or more `response_chunk` frames, then `response_end`.
`cancel` terminates an in-flight request. `request_id` correlates one HTTP
exchange, while `message_id` supports frame diagnostics and replay detection.
These ordering and identity rules require runtime enforcement; the JSON Schema
only validates individual frames.

- Create UI sessions with an authenticated POST, not an unauthenticated
  execution ID. Use a short-lived, opaque session bound to the authenticated
  user and execution. The API stores only hashes of bootstrap and cookie
  secrets. Serve mission-authored HTML/JavaScript on a distinct origin per
  execution with no Platform API cookies or bearer tokens; a shared UI origin
  does not isolate different mission applications.
- Restrict `postMessage` to exact origins and a single-use bootstrap, then
  exchange it for a host-only HttpOnly, Secure cookie. Set
  `Referrer-Policy: no-referrer` and allow framing only by the Console origin.
  Never place a reusable token in the URL.
- Restrict the proxy to the Runner-assigned loopback endpoint. Normalize and
  validate paths; strip hop-by-hop and credential headers; reject authority,
  absolute-URL and path-traversal forms. Do not forward Platform or device
  credentials to the mission.
- Preserve SSE event boundaries and disable response buffering for streaming
  routes by forwarding the byte stream unchanged. Version 1 does not accept
  inner WebSocket upgrades. Bound concurrent viewers, frame size, queued bytes
  and idle time; propagate cancellation and apply backpressure.
- Recheck user membership and execution liveness during long streams. On
  membership revocation or execution end, close the UI session and all
  associated streams. UI reconnect creates or refreshes a session only after
  authorization succeeds again.
- Console keeps execution status and stop controls outside the iframe. UI
  transport failure must not block Platform stop/status operations.

## Implementation sequence

1. Runner port assignment, readiness, the authenticated active UI status list,
   and the authenticated per-execution HTTP proxy to its fixed loopback UI port
   are implemented. The proxy streams responses and applies path, header,
   request-size and concurrency bounds.
2. The separate authenticated UI WebSocket handshake and daemon status
   reporting/reconnect are implemented. The current socket accepts hello,
   heartbeat and UI status frames; bounded request/response streaming is open.
   The daemon authenticates using its enrolled device credential; the Platform
   binds each status to the robot identity established by that connection.
3. Add Platform UI-session persistence, current-membership authorization and
   HTTP/WebSocket gateway routes. Session creation and one-time bootstrap
   exchange are implemented; cookie-authenticated proxy routes remain open.
4. Add the selected-execution entry point in Console and session lifecycle
   states. Keep UI-session authorization separate from mission operation
   permissions.
5. Test denied cross-organization access, revocation, stale/ended executions,
   SSE traffic, reconnect, multiple viewers, backpressure,
   origin isolation and mission continuity.

The UI-session and device tunnel frame schemas are under
`packages/contracts/schemas/mission-ui/v1`; representative bidirectional
frames are in `packages/contracts/fixtures/mission-ui/v1/device-frames.json`.
This is a new protocol design and is not wired into the existing command
socket. After reload, the authenticated Platform status-socket handshake and
Runner's live UI readiness were checked. End-to-end daemon status propagation
still needs server-side observability. Runner's new proxy awaits a
post-reload live check; daemon frame forwarding and the browser gateway remain
open. The v1 frame limits are 64 KiB per outer JSON message, 16 KiB decoded
per body chunk, 1 MiB per request body, four concurrent requests per viewer,
and an eight-frame bounded send queue. Response bodies stream without an
aggregate cap; idle streams expire after 120 seconds. The receiver must enforce
decoded chunk and cumulative-body limits in addition to validating the schema.
Milestone 10b remains incomplete.
