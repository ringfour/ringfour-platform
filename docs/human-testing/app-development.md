# Develop an App over the LAN

This is App development fastlane v1. Dependencies, inference runtimes and model
weights belong in the development image. Source edits travel directly from the
CLI to the enrolled Daemon over pinned HTTPS. Each restart is a normal App run
with development session, generation, source hash and image provenance. Durable
App output, artifacts, settings and Logbook remain available through existing
App APIs/Studio. This version has explicit sync/restart, not file watching.

## Operator activation

Do not erase enrollment/data volumes. In this workspace the operator performs
Platform and virtual stack rebuild/recreate operations under AGENTS.md.

```sh
cd /home/cdr/ringfour/ringfour-platform
./up.sh -d api console
```

The API applies migration `20261127124235_AppDevelopmentFastlane`.
In the development panel, Rebuild images once, then Restart each desired virtual
stack so its new HTTPS port is published. A daemon-only restart cannot add a
new port mapping. SQLite migration 23 preserves enrollment and existing Apps.
Refresh Studio. Restart the standalone development panel and reconnect its
account workers to load the new optional `ap-development` protocol suite.

Current virtual host ports:

| Stack | Development HTTPS | Status |
| --- | --- | --- |
| 1 | 8443 | 8083 |
| 2 | 8453 | 8093 |
| 3 | 8463 | 8103 |

The defaults advertise https://127.0.0.1 and publish on loopback for local WSL
use. To connect from another workstation, configure the virtual checkout `.env`
before restarting the stacks:

```dotenv
RINGFOUR_DEVELOPMENT_BIND=0.0.0.0
RINGFOUR_DEVELOPMENT_HOSTNAME=192.168.1.100
```

Use the address actually reachable on your LAN; WSL host forwarding/firewall
must route these ports. Numbered launchers select each port independently. On a
physical robot, infrastructure sets RINGFOUR_DEVELOPMENT_URL to its reachable
HTTPS origin, publishes daemon port 8085 and preserves its TLS PEM at
/data/app-development.pem (owner-only). This listener is separate from the
read-only status page and App runtime API. Do not disable certificate pinning.

## Prepare the image once

Log in as Owner or Developer and select the organization containing the robot.
Use the existing template archive/cache and hash-pinned Python artifact bundle:

```sh
ringfour-cli app init my-app --app-key my-app --repository REGISTRY/ORG/my-app
cd my-app
ringfour-cli app build --development
ringfour-cli app publish
ringfour-cli app install REVISION_UUID --robot ROBOT_UUID --config config.example.json
```

Record the revision UUID returned by publish, wait for installation success, and
use this installed development image when enabling the session. Initial image
preparation still uses the normal registry publication/install path. Source
iterations do not publish a new release. Keep a development release/version
separate from the eventual production release.

The generated editable Dockerfile has shared dependencies, development, and
release stages. Put large packages, model COPY/download/checksum steps and
inference runtimes in the common `dependencies` stage, outside /workspace:

```dockerfile
# In the dependencies stage, before FROM dependencies AS development:
COPY models/ /opt/models/
ENV MODEL_PATH=/opt/models/model.safetensors
```

List every model/build input in app.json build.files when it is copied from the
checkout; the builder streams/hashes these explicit inputs. Development images
must label eu.ringfour.development-source=/workspace, use WorkingDir /workspace,
a numeric non-root User and their own executable entrypoint. The template runs
python /workspace/main.py. /app remains present for normal App configuration.
Image authors can adapt the entrypoint to their App; no CLI/manifest override
of commands, mounts, networks, privileges or sockets is introduced. GPU/native
access still depends on the existing supported target/owner deployment policy;
fastlane itself does not add GPU device passthrough.

## Enable, sync, run, repeat

1. Stop the current App and wait for operations to complete. Open the robot in
   Studio, choose its installed development App under Development mode, and
   click Enable development mode. The session defaults to one hour. It reserves
   the robot against ordinary App starts and binding changes.
2. From the App source directory on your workstation:

```sh
ringfour-cli app dev connect --robot ROBOT_UUID
ringfour-cli app dev sync
ringfour-cli app dev restart --follow
```

Alternatively enable from CLI with `app dev enable INSTALLATION_UUID --ttl 3600`.
The TTL range is 300–28800 seconds. The session owner or organization Owner can
control it while retaining Developer authority; Operators cannot enable or
obtain source-write grants.

3. Edit main.py or other source files, run sync then restart again. Restart stops
   only that session's preceding run and assigns a new App run/container. Deleted
   files do not survive into the next fresh container. Unchanged source is
   skipped. Models/dependencies remain in the cached image. `--source src` can
   select another source tree; it becomes the root of /workspace. `--exclude NAME`
   can omit additional file/directory names.
4. Studio Recent App runs labels development runs with their source generation.
   Follow each run into ordinary logs/files/Logbook. `app dev logs --follow`
   reads the project's latest run through durable App logs. `app dev status`
   shows the current session. Standard `app artifacts` / `app artifact` commands
   continue to address those same App run IDs. Ctrl-C ends log following; it
   does not stop the App.
5. End development session in Studio or run `app dev stop`. Closure is
   asynchronous: the Daemon observes it, stops that session's App and reports
   its normal terminal state. Await that state before starting another App.
   Independent TTL expiry also stops development execution without a Platform
   connection; new source grants require authenticated Platform coordination.

Source sync omits hidden paths (.env/.git/.venv/.ringfour), __pycache__, model
and weights directories, python-wheels, node_modules, build/dist and common
model extensions. ZIP snapshots are limited to 16 MiB compressed / 32 MiB
extracted, 1024 regular files and 8 MiB per file. Put large assets in the image
instead. The CLI stores only API/robot/session/installation/run IDs and retry
metadata locally, never its raw five-minute LAN token. A broken connection can
be retried with the same operation identity; source reporting may lag until the
Platform returns. Wait for the hosted generation before restarting.

## Change dependencies or publish a release

For a dependency/model change, end the session, increment the immutable App
release in app.json, rebuild with --development, publish/install the replacement
and start a new session. Ordinary source-only edits do not require this.

For production, end the session, increment the release again, and run build
without --development, then publish/install normally. The final release image
contains the source and runs without any development session or LAN uploader.

## Verification and negative checks

Verify two source edits produce different run IDs/hashes/generations with the
same image digest, and ordinary App logs still contain the expected output.
Verify a competing normal App start is refused, another developer cannot take
over the session, Operators cannot enable it, stale session IDs cannot revoke a
newer session, and grant/certificate failure never falls back to insecure HTTP.
Verify ending/expiring the session stops its App and leaves release history,
model layers, credentials and robot enrollment intact. These checks do not
require deleting the robot or resetting volumes.
