# Ringfour Console

The Console is an internal development UI for the Platform API. It uses the
Auth0 Web SPA with Authorization Code and PKCE; the API still enforces all
organization roles. It offers fleet, mission, deployment, execution and
workspace inspection. The current app is development-only and is not published
as a production Console.

## Get started

Configure the Auth0 Web client and server stack as described in
[development setup](../planning/development-legacy.md#auth0-milestone-01). From the
repository root:

```sh
./up.sh
```

Open `http://localhost:5173/` and sign in. The Vite server proxies `/api` to
the Platform; Compose supplies `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`,
`VITE_AUTH0_AUDIENCE`, `VITE_CONSOLE_ORIGIN` and `API_PROXY_TARGET`.
The default Console origin is `http://localhost:5173`, matching the Auth0 SPA
callback, logout and web-origin registration. Opening the development server by
LAN IP redirects to that canonical origin before Auth0 initializes. If dependencies change and a
recreated container still uses an old anonymous `/app/node_modules` volume,
renew the Console's anonymous volume when recreating that service.

## Develop

```sh
cd apps/console
npm ci
npm run lint
npm run typecheck
npm test
npm run build
```

`src/App.tsx` owns navigation; `src/components/` contains the views and
`src/lib/api.ts` the authenticated API helper. UI styling follows the sibling
`ringfour/design/design_system` guidelines, with assets included in this app.
See the [mission testing guide](../human-testing/06.md) for the current
Console workflow. The production build intentionally displays the
restricted-development message rather than an operational Console.

## Sample screenshots

Run the development Console, then generate screenshots with a browser-only
sample workspace:

```sh
cd apps/console
LD_LIBRARY_PATH=/tmp/rf-sdk-review/libs/usr/lib/x86_64-linux-gnu npm run screenshot:demo
```

The script writes Overview, Runs, Robots, Apps, Timeline, a short-run tooltip,
an opened Timeline event card, and People images to
`screenshots/demo/` at the repository root. Set `CONSOLE_URL` to use another
local development origin, `SCREENSHOT_DIR` to change the output directory, or
`SCREENSHOT_WIDTH` to capture another viewport width.
The sample workspace, people, robots, apps, and runs exist only inside this
browser session. The script intercepts sample API reads and makes no changes
to PostgreSQL or the live workspace. The library path above supplies missing
Chromium libraries on this development host; omit it on hosts that have those
libraries installed. The script uses Playwright Chromium; install the browser
with `npx playwright install chromium` where the host supports it.
