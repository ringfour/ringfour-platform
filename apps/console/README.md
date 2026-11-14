# Ringfour Console

The Console is an internal development UI for the Platform API. It uses the
Auth0 Web SPA with Authorization Code and PKCE; the API still enforces all
organization roles. It offers fleet, mission, deployment, execution and
workspace inspection. The current app is development-only and is not published
as a production Console.

## Get started

Configure the Auth0 Web client and local stack as described in
[development setup](../../docs/development.md#auth0-milestone-01). From the
repository root, with the required Auth0 configuration and shared Runner token:

```sh
docker compose -f infra/compose/docker-compose.yml up -d --build
```

Open `http://localhost:5173/` and sign in. The Vite server proxies `/api` to
the Platform; Compose supplies `VITE_AUTH0_DOMAIN`, `VITE_AUTH0_CLIENT_ID`,
`VITE_AUTH0_AUDIENCE` and `API_PROXY_TARGET`. If dependencies change and a
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
See the [mission testing guide](../../docs/human-testing/06.md) for the current
Console workflow. The production build intentionally displays the
restricted-development message rather than an operational Console.
