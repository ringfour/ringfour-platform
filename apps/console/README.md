# Ringfour Console

The Console is an authenticated internal web interface for workspace and robot
operations. It is not a production-hosted Studio release. Its App workflow
migration is AP-11; the active scope and operator gates are in the
[Apps pivot plan](../../docs/milestones/apps-pivot.md).

Configure the Auth0 Web client and local API using
[Platform development setup](../../docs/development.md#auth0-setup). From this
directory:

```sh
npm ci
npm run dev
npm run build
```

Source navigation starts in `src/App.tsx`; authenticated API access is in
`src/lib/api.ts`. Follow the existing Ringfour interface tokens and design
system guidance. The former mission-centered Console design and acceptance
notes are preserved in the
[archive](../../docs/archive/legacy-mission-workflows/README.md).
