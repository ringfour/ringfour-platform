# Ringfour Studio

`apps/console` is the deployable, authenticated Ringfour Studio. Auth0 and server workspace roles
remain mandatory in development and production.

The Platform stack serves the compiled production Studio. For source development,
run Vite separately with a registered Auth0 origin and the API proxy configured:

```sh
npm ci
npm run dev
npm run typecheck
npm test
npm run lint
```

Configure the Auth0 SPA client, audience and exact allowed origin as described
in [Platform development setup](../../docs/development.md#auth0-setup). The local
Compose recipe selects the Dockerfile's `production` target, passing the public
Auth0 settings as build arguments. Its host port remains 5173; Nginx listens on
container port 80. The `development` target remains available for explicit source
previews.
Workspace and installation identity live in URLs; ordinary links support browser
Back, refresh and direct entry.

## Production bundle and container

The default Dockerfile target is `production`: a compiled static bundle served
by pinned Nginx. Auth0 public SPA configuration is supplied at build time, not as
runtime Vite variables. These values are public configuration, never a client
secret. Register each supported Studio origin in the Auth0 SPA's allowed
callback, logout and web origins. The browser keeps the origin it opened,
including LAN addresses; it is not redirected to a compiled canonical origin.

Operator build command, from the Platform root with the appropriate public
configuration exported:

```sh
docker build --target production -t ringfour-studio:local \
  --build-arg VITE_AUTH0_DOMAIN="$AUTH0_DOMAIN" \
  --build-arg VITE_AUTH0_CLIENT_ID="$AUTH0_WEB_CLIENT_ID" \
  --build-arg VITE_AUTH0_AUDIENCE="https://api.ringfour.eu" \
  apps/console
```

Serve container port 80 behind the deployment's HTTPS ingress. Runtime
`API_PROXY_TARGET` selects the API origin (default `http://api:8080` on the
server network). It must have no trailing slash or path prefix. Requests under
`/api/` are forwarded with the bearer token and the `/api` prefix removed.
Navigation falls back to `index.html`; missing `/assets/` files return 404.
Production does not serve the archived Studio prototype or a development-only
screen. `nginx.conf.template` defines the serving behavior.

Vite development builds include development-time checks and effect replay. Use
the compiled target for normal use and performance checks. Nginx compresses the
JavaScript/CSS/SVG responses and caches hashed assets. Stack operations are performed by the operator.

## Browser and production fixtures

From this directory, `npm run screenshot:demo` checks a running development
preview (default `http://localhost:5173`; override with `CONSOLE_URL`). Its Auth0
and API interceptions exist only in that Playwright browser context. The fixture
uses Camera Snapshot Loop on virt2 and Robot B, an imported private App, and a
small synthetic PNG to exercise receipt verification and preview. It creates
no real API records and is not live Auth0/robot acceptance.

From the Platform root, `node scripts/test-studio-browser.mjs` starts a disposable development preview and runs the same fixture. On Linux, set `RINGFOUR_BROWSER_LIBRARY_PATH` if Chromium needs libraries installed outside the system search path. Screenshots from the standalone demo are written to `apps/console/test-results/browser/`.

## Supplied Studio views

Page structure follows the owner's `ringfour-robot-studio` design at `1b99764`.
See [source provenance](src/views/README.md). Platform keeps its own CSS tokens,
Auth0, canonical routes and actual API operations. The design's mock store and
SSR infrastructure are excluded. Reference App artwork and labelled Store example
imagery are local assets; Files and run Results only show verified API artifacts.
`lucide-react` is pinned to 0.575.0; bundled notices are in `public/lucide-LICENSE.txt`.

After this dependency/view change, the operator can rebuild only Console with
`./up.sh -d --no-deps console` from the Platform root. No device restart is needed
for these views.


Shared page/grid/row patterns and migration examples are in
[Studio layout template](../../docs/studio-layout.md). Avoid adding a second gutter
or a centered form inside App tabs.


## Extending the frontend

Use [the source guide](src/README.md) for the feature/module map, controller/view
boundaries, `CollectionPage`/`StudioPage` templates and shared `SchemaFields`.
The source guide also covers auth, state ownership and route tests.
