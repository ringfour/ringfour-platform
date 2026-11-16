# Server stack

This basic stack runs PostgreSQL, the Platform API, and the internal Console.
It intentionally contains no daemon or Runner; those run on the robot. The
Console service uses the app's Vite development server and is for internal
development/basic self-hosting only, not production publishing.

Set `AUTH0_DOMAIN` and `AUTH0_WEB_CLIENT_ID`, then start it with:

```sh
./up.sh
```

The wrapper generates a stable random PostgreSQL password if one is not
already set. If invoking Compose directly, provide `POSTGRES_PASSWORD` yourself.

The API and Console are published on `${API_PORT:-8080}` and
`${CONSOLE_PORT:-5173}`. Set `CONSOLE_ORIGIN` to the registered Auth0 SPA
origin if it differs from the default. PostgreSQL is reachable only on the
Compose network. For local use, storage defaults to the persistent
filesystem volume. For a hosted deployment, set `STORAGE_BACKEND=s3`,
`STORAGE_S3_ENDPOINT`, `STORAGE_S3_REGION`, and `STORAGE_S3_BUCKET`; provide
object-store credentials through the AWS SDK provider chain and put the API
behind the deployment's HTTPS ingress/reverse proxy. `up.sh` creates and reuses
a random local PostgreSQL password in the ignored `.env.server` file unless
`POSTGRES_PASSWORD` is already set.

`infra/compose/docker-compose.yml` is the separate local integration stack. It
also includes a local daemon and Runner to test device flows; do not deploy
that test topology as the server.
