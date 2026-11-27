diff --git a/.github/workflows/components.yml b/.github/workflows/components.yml
--- a/.github/workflows/components.yml
+++ b/.github/workflows/components.yml
@@ -71,3 +71,145 @@
       - uses: actions/checkout@v4
       - uses: astral-sh/setup-uv@v6
       - run: uv run --frozen --python 3.13 --with pytest pytest -q tests
+
+  release-scripts:
+    runs-on: ubuntu-latest
+    steps:
+      - uses: actions/checkout@v4
+      - run: |
+          for script in cli-installer.sh deployment/deployment.sh deployment/robot-update.sh; do
+            sh -n "$script"
+          done
+          python -m py_compile scripts/package-cli-release.py
+      - shell: pwsh
+        run: |
+          $tokens = $null
+          $errors = $null
+          $null = [System.Management.Automation.Language.Parser]::ParseFile(
+            (Resolve-Path ./cli-installer.ps1), [ref]$tokens, [ref]$errors
+          )
+          if ($errors) { throw ($errors -join "`n") }
+
+  release-build:
+    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
+    needs: [api, console, contracts, python-apps, runtime, release-scripts]
+    runs-on: ubuntu-latest
+    permissions:
+      contents: read
+      packages: write
+    env:
+      AUTH0_DOMAIN: ${{ vars.AUTH0_DOMAIN }}
+      AUTH0_CLI_CLIENT_ID: ${{ vars.AUTH0_CLI_CLIENT_ID }}
+      AUTH0_WEB_CLIENT_ID: ${{ vars.AUTH0_WEB_CLIENT_ID }}
+      AUTH0_AUDIENCE: ${{ vars.AUTH0_AUDIENCE || 'https://api.ringfour.eu' }}
+      RINGFOUR_API_URL: ${{ vars.RINGFOUR_API_URL }}
+      RELEASE_IMAGE_TAG: ci-${{ github.run_id }}-${{ github.run_attempt }}
+      IMAGE_PREFIX: ghcr.io/ringfour/ringfour-platform
+    steps:
+      - uses: actions/checkout@v4
+      - uses: astral-sh/setup-uv@v6
+      - uses: docker/setup-qemu-action@v3
+        with:
+          platforms: arm64
+      - uses: docker/setup-buildx-action@v3
+      - uses: docker/login-action@v3
+        with:
+          registry: ghcr.io
+          username: ${{ github.actor }}
+          password: ${{ secrets.GITHUB_TOKEN }}
+      - name: Package and test CLI
+        run: |
+          python scripts/package-cli-release.py
+          uv build --wheel --python 3.13 --out-dir "$PWD/dist" apps/cli
+          uv tool run --isolated --python 3.13 --from "$(realpath dist/*.whl)" ringfour-cli --help
+      - uses: docker/build-push-action@v6
+        id: api
+        with:
+          context: .
+          file: apps/api/Dockerfile
+          platforms: linux/amd64
+          push: true
+          tags: ${{ env.IMAGE_PREFIX }}-api:${{ env.RELEASE_IMAGE_TAG }}
+      - uses: docker/build-push-action@v6
+        id: studio
+        with:
+          context: apps/console
+          target: production
+          platforms: linux/amd64
+          push: true
+          tags: ${{ env.IMAGE_PREFIX }}-studio:${{ env.RELEASE_IMAGE_TAG }}
+          build-args: |
+            VITE_AUTH0_DOMAIN=${{ env.AUTH0_DOMAIN }}
+            VITE_AUTH0_CLIENT_ID=${{ env.AUTH0_WEB_CLIENT_ID }}
+            VITE_AUTH0_AUDIENCE=${{ env.AUTH0_AUDIENCE }}
+      - uses: docker/build-push-action@v6
+        id: daemon
+        with:
+          context: apps/daemon
+          platforms: linux/amd64,linux/arm64
+          push: true
+          tags: ${{ env.IMAGE_PREFIX }}-daemon:${{ env.RELEASE_IMAGE_TAG }}
+      - name: Assemble release and smoke test the images
+        env:
+          API_DIGEST: ${{ steps.api.outputs.digest }}
+          STUDIO_DIGEST: ${{ steps.studio.outputs.digest }}
+          DAEMON_DIGEST: ${{ steps.daemon.outputs.digest }}
+        run: |
+          printf 'RINGFOUR_API_IMAGE=%s-api@%s\nRINGFOUR_STUDIO_IMAGE=%s-studio@%s\n' \
+            "$IMAGE_PREFIX" "$API_DIGEST" "$IMAGE_PREFIX" "$STUDIO_DIGEST" > dist/images.env
+          printf '%s-daemon@%s\n' "$IMAGE_PREFIX" "$DAEMON_DIGEST" > dist/daemon-image.txt
+          cp deployment/compose.yml deployment/deployment.sh deployment/.env.example deployment/README.md dist/
+          cp deployment/robot-update.sh deployment/robot-update.service deployment/robot-update.timer dist/
+          tar -C dist -czf deployment.tar.gz compose.yml deployment.sh .env.example README.md images.env
+          mv deployment.tar.gz dist/
+          rm dist/compose.yml dist/deployment.sh dist/.env.example dist/README.md dist/images.env
+          (cd dist && sha256sum ./*.whl ./deployment.tar.gz ./daemon-image.txt ./robot-update.* | sed 's|  \./|  |' > SHA256SUMS)
+          export POSTGRES_PASSWORD="$(openssl rand -hex 32)"
+          export CONSOLE_ORIGIN=http://127.0.0.1:5173
+          smoke="$(mktemp -d)"
+          tar -xzf dist/deployment.tar.gz -C "$smoke"
+          compose=(docker compose -p ringfour-release-smoke --env-file "$smoke/images.env" -f "$smoke/compose.yml")
+          cleanup() {
+            docker rm -f ringfour-daemon-smoke >/dev/null 2>&1 || true
+            "${compose[@]}" down -v
+          }
+          trap cleanup EXIT
+          docker run -d --name ringfour-daemon-smoke -p 127.0.0.1:18081:8081 \
+            -e RINGFOUR_APPS_ENABLED=false "$IMAGE_PREFIX-daemon@$DAEMON_DIGEST"
+          curl -fsS --retry 20 --retry-delay 2 --retry-all-errors http://127.0.0.1:18081/health/live
+          "${compose[@]}" up -d --wait --wait-timeout 180
+          curl -fsS http://127.0.0.1:5173/ >/dev/null
+          curl -fsS --retry 20 --retry-delay 2 --retry-all-errors http://127.0.0.1:5173/api/health/ready
+      - uses: actions/upload-artifact@v4
+        with:
+          name: platform-release
+          overwrite: true
+          path: dist/
+          if-no-files-found: error
+
+  release-publish:
+    if: github.event_name == 'push' && github.ref == 'refs/heads/main'
+    needs: release-build
+    runs-on: ubuntu-latest
+    permissions:
+      contents: write
+    concurrency:
+      group: platform-release-publish
+      cancel-in-progress: false
+    env:
+      GH_TOKEN: ${{ secrets.GITHUB_TOKEN }}
+      GH_REPO: ${{ github.repository }}
+    steps:
+      - uses: actions/download-artifact@v4
+        with:
+          name: platform-release
+          path: dist
+      - name: Publish complete build
+        run: |
+          if [ "$(gh api "repos/$GH_REPO/git/ref/heads/main" --jq .object.sha)" != "$GITHUB_SHA" ]; then
+            echo 'A newer main commit is queued. Skipping publication.'
+            exit 0
+          fi
+          tag="main-$GITHUB_RUN_ID-$GITHUB_RUN_ATTEMPT"
+          gh release create "$tag" dist/* --target "$GITHUB_SHA" --latest \
+            --title "Main ${GITHUB_SHA:0:12}" --notes "Source commit: $GITHUB_SHA"
diff --git a/apps/cli/README.md b/apps/cli/README.md
--- a/apps/cli/README.md
+++ b/apps/cli/README.md
@@ -6,6 +6,17 @@
 configured Engine.
 
 ## Install and sign in
+
+Install the latest main build on Linux or macOS:
+
+```sh
+curl -fsSL https://raw.githubusercontent.com/ringfour/ringfour-platform/main/cli-installer.sh | sh
+ringfour-cli login
+```
+
+On native Windows use [cli-installer.ps1](../../cli-installer.ps1). Release
+builds include public Auth0 and API defaults. The environment and per-user
+configuration can override them.
 
 From the Platform checkout, run the CLI directly:
 
diff --git a/apps/cli/src/ringfour_cli/config.py b/apps/cli/src/ringfour_cli/config.py
--- a/apps/cli/src/ringfour_cli/config.py
+++ b/apps/cli/src/ringfour_cli/config.py
@@ -40,11 +40,15 @@
     config: dict = {}
     if path.exists():
         config = json.loads(path.read_text())
+    defaults_path = Path(__file__).with_name("release_defaults.json")
+    defaults = json.loads(defaults_path.read_text()) if defaults_path.is_file() else {}
     return Settings(
-        api_url=_pick("RINGFOUR_API_URL", config, "api_url", DEFAULT_API_URL),
-        auth0_domain=_pick("RINGFOUR_AUTH0_DOMAIN", config, "auth0_domain"),
-        auth0_client_id=_pick("RINGFOUR_AUTH0_CLIENT_ID", config, "auth0_client_id"),
-        auth0_audience=_pick("RINGFOUR_AUTH0_AUDIENCE", config, "auth0_audience", DEFAULT_AUDIENCE),
+        api_url=_pick("RINGFOUR_API_URL", config, "api_url", defaults.get("api_url", DEFAULT_API_URL)),
+        auth0_domain=_pick("RINGFOUR_AUTH0_DOMAIN", config, "auth0_domain", defaults.get("auth0_domain")),
+        auth0_client_id=_pick("RINGFOUR_AUTH0_CLIENT_ID", config, "auth0_client_id", defaults.get("auth0_client_id")),
+        auth0_audience=_pick(
+            "RINGFOUR_AUTH0_AUDIENCE", config, "auth0_audience", defaults.get("auth0_audience", DEFAULT_AUDIENCE)
+        ),
         config_path=path,
         config=config,
     )
diff --git a/cli-installer.ps1 b/cli-installer.ps1
new file mode 100644
--- /dev/null
+++ b/cli-installer.ps1
@@ -0,0 +1,43 @@
+$ErrorActionPreference = 'Stop'
+$repo = 'https://github.com/ringfour/ringfour-platform'
+$release = Invoke-RestMethod 'https://api.github.com/repos/ringfour/ringfour-platform/releases/latest'
+$tag = $release.tag_name
+if ($tag -cnotmatch '^main-\d+-\d+$') { throw 'No Ringfour main release found' }
+$assets = "$repo/releases/download/$tag"
+$directory = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid()))
+try {
+    $checksumsPath = Join-Path $directory.FullName 'SHA256SUMS'
+    Invoke-WebRequest "$assets/SHA256SUMS" -OutFile $checksumsPath -UseBasicParsing
+    $checksums = Get-Content -LiteralPath $checksumsPath
+    $entries = @($checksums | Where-Object { $_ -cmatch '^[a-f0-9]{64}  ringfour_cli-[a-zA-Z0-9.+_]+-py3-none-any\.whl$' })
+    if ($entries.Count -ne 1) { throw "Expected one CLI wheel in release $tag" }
+    $hash, $wheel = $entries[0] -split '  ', 2
+    $wheelPath = Join-Path $directory.FullName $wheel
+    Invoke-WebRequest "$assets/$wheel" -OutFile $wheelPath -UseBasicParsing
+    if ((Get-FileHash -Algorithm SHA256 $wheelPath).Hash -ne $hash) { throw 'CLI wheel checksum mismatch' }
+
+    $uv = (Get-Command uv -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1).Source
+    if (!$uv) {
+        $uvDir = Join-Path $env:USERPROFILE '.local\bin'
+        $uv = Join-Path $uvDir 'uv.exe'
+        if (!(Test-Path -LiteralPath $uv)) {
+            $installer = Join-Path $directory.FullName 'install-uv.ps1'
+            Invoke-WebRequest 'https://astral.sh/uv/0.12.1/install.ps1' -OutFile $installer -UseBasicParsing
+            $env:UV_INSTALL_DIR = $uvDir
+            $env:UV_NO_MODIFY_PATH = '1'
+            try {
+                & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
+                if ($LASTEXITCODE -ne 0) { throw 'uv installation failed' }
+            } finally {
+                Remove-Item Env:UV_INSTALL_DIR, Env:UV_NO_MODIFY_PATH -ErrorAction SilentlyContinue
+            }
+        }
+    }
+    & $uv tool install --force --python 3.13 $wheelPath
+    if ($LASTEXITCODE -ne 0) { throw 'Ringfour CLI installation failed' }
+    & $uv tool update-shell
+    if ($LASTEXITCODE -ne 0) { throw 'Unable to add CLI tools to PATH' }
+    Write-Output 'Open a new terminal and run ringfour-cli login.'
+} finally {
+    [IO.Directory]::Delete($directory.FullName, $true)
+}
diff --git a/cli-installer.sh b/cli-installer.sh
new file mode 100755
--- /dev/null
+++ b/cli-installer.sh
@@ -0,0 +1,50 @@
+#!/bin/sh
+set -eu
+
+case "$(uname -s)-$(uname -m)" in
+  Linux-x86_64|Linux-aarch64|Darwin-x86_64|Darwin-arm64) ;;
+  *) echo "Unsupported platform. Use cli-installer.ps1 on native Windows." >&2; exit 1 ;;
+esac
+
+repo=https://github.com/ringfour/ringfour-platform
+release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/releases/latest")
+tag=${release_url##*/}
+printf '%s\n' "$tag" | grep -Eq '^main-[0-9]+-[0-9]+$' || {
+  echo "No Ringfour main release found" >&2
+  exit 1
+}
+assets="$repo/releases/download/$tag"
+
+tmp=$(mktemp -d)
+trap 'rm -rf "$tmp"' EXIT
+curl -fsSL "$assets/SHA256SUMS" -o "$tmp/SHA256SUMS"
+wheel=$(awk '$2 ~ /^ringfour_cli-[[:alnum:].+_]+-py3-none-any\.whl$/ {print $2}' "$tmp/SHA256SUMS")
+case "$wheel" in
+  ''|*[!a-zA-Z0-9._+-]*) echo "Expected one CLI wheel in release $tag" >&2; exit 1 ;;
+esac
+curl -fsSL "$assets/$wheel" -o "$tmp/$wheel"
+(
+  cd "$tmp"
+  awk -v wheel="$wheel" '$2 == wheel {print}' SHA256SUMS > wheel.sha256
+  if command -v sha256sum >/dev/null 2>&1; then
+    sha256sum -c wheel.sha256
+  else
+    shasum -a 256 -c wheel.sha256
+  fi
+)
+
+uv_bin=$(command -v uv || true)
+if [ -z "$uv_bin" ]; then
+  uv_bin="$HOME/.local/bin/uv"
+  if [ ! -x "$uv_bin" ]; then
+    curl -fsSL https://astral.sh/uv/0.12.1/install.sh -o "$tmp/install-uv.sh"
+    UV_INSTALL_DIR="$HOME/.local/bin" UV_NO_MODIFY_PATH=1 sh "$tmp/install-uv.sh"
+  fi
+fi
+"$uv_bin" tool install --force --python 3.13 "$tmp/$wheel"
+"$uv_bin" tool update-shell
+tool_dir=$("$uv_bin" tool dir --bin)
+case ":$PATH:" in
+  *":$tool_dir:"*) printf 'Run ringfour-cli login\n' ;;
+  *) printf 'Open a new shell and run ringfour-cli login, or run "%s/ringfour-cli" login now.\n' "$tool_dir" ;;
+esac
diff --git a/deployment/.env.example b/deployment/.env.example
new file mode 100644
--- /dev/null
+++ b/deployment/.env.example
@@ -0,0 +1,10 @@
+# Set a persistent password before first boot. Keep this file on the N100.
+POSTGRES_PASSWORD=
+AUTH0_DOMAIN=
+AUTH0_AUDIENCE=https://api.ringfour.eu
+CONSOLE_ORIGIN=
+API_PORT=8080
+CONSOLE_PORT=5173
+
+# Enable after configuring a registry reachable from the API and robots.
+APPS_INSTALLATIONS_ENABLED=false
diff --git a/deployment/compose.yml b/deployment/compose.yml
new file mode 100644
--- /dev/null
+++ b/deployment/compose.yml
@@ -0,0 +1,72 @@
+name: ringfour-server
+
+services:
+  postgres:
+    image: postgres:18
+    restart: unless-stopped
+    environment:
+      POSTGRES_DB: ringfour
+      POSTGRES_USER: ringfour
+      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD:?Set a strong POSTGRES_PASSWORD}
+    volumes:
+      - pgdata:/var/lib/postgresql
+    healthcheck:
+      test: ["CMD-SHELL", "pg_isready -U ringfour -d ringfour"]
+      interval: 2s
+      timeout: 3s
+      retries: 30
+
+  api:
+    image: ${RINGFOUR_API_IMAGE:?Missing release image pins}
+    restart: unless-stopped
+    environment:
+      ASPNETCORE_ENVIRONMENT: Production
+      ConnectionStrings__Postgres: Host=postgres;Port=5432;Database=ringfour;Username=ringfour;Password=${POSTGRES_PASSWORD:?Set a strong POSTGRES_PASSWORD}
+      Auth0__Domain: ${AUTH0_DOMAIN:?Set AUTH0_DOMAIN}
+      Auth0__Audience: ${AUTH0_AUDIENCE:-https://api.ringfour.eu}
+      Email__SmtpHost: ${SMTP_HOST:-}
+      Email__SmtpPort: ${SMTP_PORT:-587}
+      Email__SmtpUsername: ${SMTP_USERNAME:-}
+      Email__SmtpPassword: ${SMTP_PASSWORD:-}
+      Email__FromAddress: ${SMTP_FROM_ADDRESS:-}
+      Email__FromName: ${SMTP_FROM_NAME:-Ringfour}
+      Email__ConsoleOrigin: ${CONSOLE_ORIGIN:?Set the public Studio origin}
+      Storage__Backend: ${STORAGE_BACKEND:-filesystem}
+      Storage__FilesystemRoot: /storage
+      Storage__S3__Endpoint: ${STORAGE_S3_ENDPOINT:-}
+      Storage__S3__Region: ${STORAGE_S3_REGION:-}
+      Storage__S3__Bucket: ${STORAGE_S3_BUCKET:-}
+      Apps__InstallationsEnabled: ${APPS_INSTALLATIONS_ENABLED:-false}
+      Apps__OfficialPublisherOrganizationId: ${APPS_OFFICIAL_PUBLISHER_ORGANIZATION_ID:-}
+      Apps__LiveSettingsEnabled: ${APPS_LIVE_SETTINGS_ENABLED:-false}
+      Apps__Registries__0__AllowHttp: ${APPS_REGISTRY_ALLOW_HTTP:-false}
+      Apps__Registries__0__ApiEndpoint: ${APPS_REGISTRY_API_ENDPOINT:-}
+      Apps__Registries__0__Host: ${APPS_REGISTRY_HOST:-}
+      Apps__Registries__0__RepositoryPrefix: ${APPS_REGISTRY_REPOSITORY_PREFIX:-}
+      Apps__Registries__0__PublisherOrganizationId: ${APPS_REGISTRY_PUBLISHER_ORGANIZATION_ID:-}
+      Apps__Registries__0__AnonymousPull: ${APPS_REGISTRY_ANONYMOUS_PULL:-false}
+      Apps__Registries__0__TokenEndpoint: ${APPS_REGISTRY_TOKEN_ENDPOINT:-}
+      Apps__Registries__0__Service: ${APPS_REGISTRY_SERVICE:-}
+      Apps__Registries__0__Username: ${APPS_REGISTRY_USERNAME:-}
+      Apps__Registries__0__Password: ${APPS_REGISTRY_PASSWORD:-}
+    depends_on:
+      postgres:
+        condition: service_healthy
+    ports:
+      - "127.0.0.1:${API_PORT:-8080}:8080"
+    volumes:
+      - api-storage:/storage
+
+  console:
+    image: ${RINGFOUR_STUDIO_IMAGE:?Missing release image pins}
+    restart: unless-stopped
+    environment:
+      API_PROXY_TARGET: http://api:8080
+    depends_on:
+      - api
+    ports:
+      - "127.0.0.1:${CONSOLE_PORT:-5173}:80"
+
+volumes:
+  pgdata:
+  api-storage:
diff --git a/deployment/deployment.sh b/deployment/deployment.sh
new file mode 100755
--- /dev/null
+++ b/deployment/deployment.sh
@@ -0,0 +1,48 @@
+#!/bin/sh
+set -eu
+
+directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
+config=${RINGFOUR_DEPLOYMENT_ENV:-"$directory/.env"}
+if [ ! -r "$config" ]; then
+  echo "Create $config using the deployment .env.example before running this script." >&2
+  exit 1
+fi
+exec 9>"$directory/.deployment.lock"
+flock -n 9 || { echo "A deployment is already running" >&2; exit 1; }
+
+repo=https://github.com/ringfour/ringfour-platform
+if [ "$#" -eq 0 ]; then
+  release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/releases/latest")
+  tag=${release_url##*/}
+elif [ "$#" -eq 1 ]; then
+  tag=$1
+else
+  echo "Usage: $0 [main-release-tag]" >&2
+  exit 1
+fi
+printf '%s\n' "$tag" | grep -Eq '^main-[0-9]+-[0-9]+$' || { echo "Invalid main release tag" >&2; exit 1; }
+assets="$repo/releases/download/$tag"
+stage=$(mktemp -d)
+trap 'rm -rf "$stage"' EXIT
+curl -fsSL "$assets/SHA256SUMS" -o "$stage/SHA256SUMS"
+curl -fsSL "$assets/deployment.tar.gz" -o "$stage/deployment.tar.gz"
+(
+  cd "$stage"
+  awk '$2 == "deployment.tar.gz"' SHA256SUMS > deployment.sha256
+  sha256sum -c deployment.sha256
+  tar -xzf deployment.tar.gz
+)
+
+# Keep image pins authoritative even when running from an interactive shell.
+unset RINGFOUR_API_IMAGE RINGFOUR_STUDIO_IMAGE
+compose() {
+  docker compose -p ringfour-server --env-file "$config" --env-file "$stage/images.env" \
+    -f "$stage/compose.yml" "$@"
+}
+
+compose config --quiet
+compose pull
+compose up -d --no-build --wait --wait-timeout 180
+studio_address=$(compose port console 80)
+curl -fsS --retry 15 --retry-delay 2 --retry-all-errors "http://$studio_address/api/health/ready"
+printf '\nRunning Ringfour release %s\n' "$tag"
diff --git a/deployment/README.md b/deployment/README.md
new file mode 100644
--- /dev/null
+++ b/deployment/README.md
@@ -0,0 +1,104 @@
+# Platform delivery from main
+
+Every push to main runs the Platform component checks. A passing run builds a
+Python 3.13 CLI wheel, amd64 API and Studio images and amd64/arm64 Daemon
+images. It tests the wheel entry point, the Daemon startup and the assembled
+server stack. GitHub Releases' latest pointer exposes the complete build.
+The release tag identifies the run and attempt. Each release contains
+checksums, a server bundle with API and Studio digest pins and the Daemon
+digest. Publish retries and new runs use distinct artifact versions.
+
+## One-time GitHub and Auth0 setup
+
+Set these GitHub Actions repository variables.
+
+| Variable | Value |
+| --- | --- |
+| `AUTH0_DOMAIN` | Auth0 tenant hostname |
+| `AUTH0_CLI_CLIENT_ID` | Dedicated Native public client ID |
+| `AUTH0_WEB_CLIENT_ID` | Studio SPA client ID |
+| `AUTH0_AUDIENCE` | API identifier, defaults to `https://api.ringfour.eu` |
+| `RINGFOUR_API_URL` | Public HTTPS API base used by the CLI |
+
+Enable Device Code and Refresh Token grants for the CLI and use token endpoint
+authentication None. Enable offline access for the API if refresh tokens are
+required. Register the public Studio callback, logout and web origins for the
+SPA. The build values are public. Runtime passwords and issued user tokens are
+credentials. The CLI keeps issued tokens in the OS credential store.
+
+The workflow uses its GitHub token for publication. Set GHCR package
+visibility to public for anonymous host pulls, or provision read-only
+registry authentication on all pulling hosts. GitHub Releases hosts the
+separate CLI wheel and deployment assets.
+
+## CLI
+
+On supported glibc Linux or macOS machines with curl:
+
+```sh
+curl -fsSL https://raw.githubusercontent.com/ringfour/ringfour-platform/main/cli-installer.sh | sh
+```
+
+On native Windows, run `cli-installer.ps1` from the repository. Both installers
+verify the wheel and use uv to provision Python 3.13 and an isolated CLI
+environment. Run `ringfour-cli login` after installation. Linux desktop and
+headless login needs a working OS keyring backend. The API and Auth0 defaults
+can be overridden with the existing CLI configuration or environment.
+
+## N100
+
+Copy `deployment.sh` and `.env.example` to a dedicated directory on the
+Linux host. Copy the example to `.env`, restrict its permissions, and set
+the persistent PostgreSQL password, Auth0 domain and the HTTPS Studio
+origin. The host needs Docker Compose v2, curl, flock, tar and sha256sum.
+Run `chmod +x deployment.sh` once if copied without its executable bit.
+
+Run `./deployment.sh` for the latest published build, or
+`./deployment.sh main-<run-id>-<attempt>` for a specific one. The script
+pulls the matching API and Studio images and applies Compose with the
+stable project name `ringfour-server`. It checks API readiness through
+Studio. The project keeps PostgreSQL and API storage in named volumes.
+When adopting an existing host, confirm the Compose project name and
+back up the database and API files before the first run. Review migration
+compatibility and restore from backup when required before deploying an
+older release.
+
+The ports bind to loopback. Set up the host HTTPS reverse proxy to Studio
+and to the API base configured in Actions, with WebSocket upgrades for
+the Daemon connection to the API. Configure an App registry reachable by
+the API and the robot engines before enabling App installations. The
+development registry and Adminer live under `infra/server`.
+
+## Robot Daemon
+
+Keep the robot's existing Compose project, socket permissions, Agent network
+and durable data mounts. Set the existing Daemon service's `image` to the
+Compose variable `RINGFOUR_DAEMON_IMAGE`. Start with a digest-pinned release.
+Create a pin file named by appending `.image` to the robot Compose env file,
+containing `RINGFOUR_DAEMON_IMAGE=ghcr.io/...@sha256:...`. Pass both files
+to Compose at boot, with the pin file last. Keep that same project name,
+Compose file and environment when booting and updating the robot.
+
+Take the updater and systemd units from the same GitHub Release and verify
+them against `SHA256SUMS`. Install `robot-update.sh` as an executable under `/usr/local/lib/ringfour`
+and the included service and timer under `/etc/systemd/system`. Create
+`/etc/ringfour/robot-updater.env` with these values.
+
+| Variable | Value |
+| --- | --- |
+| `RINGFOUR_ROBOT_COMPOSE_FILE` | Absolute path to the enrolled robot's Compose file |
+| `RINGFOUR_ROBOT_ENV` | Absolute path to the Compose environment file |
+| `RINGFOUR_ROBOT_PROJECT` | Existing Compose project name |
+| `RINGFOUR_DAEMON_READY_URL` | Host-reachable private Daemon `/health/ready` URL |
+| `RINGFOUR_ROBOT_MAINTENANCE` | Absolute path to the robot operator's maintenance wrapper |
+| `RINGFOUR_DAEMON_SERVICE` | Optional Compose service name, defaults to daemon |
+
+The robot needs Docker Compose v2, curl, jq, flock and sha256sum. The
+maintenance wrapper receives the update command as arguments. It must
+defer while the robot is busy and hold the site's maintenance gate against
+new work for the lifetime of that command. Enable the timer after that gate
+is available and after confirming compatibility with the API currently
+deployed to the N100. The updater follows the latest complete main release
+and updates only the Daemon service. It writes the boot pin after readiness
+passes. A failed readiness check leaves the previous pin available. Review
+logs and SQLite migration compatibility before restarting the previous image.
diff --git a/deployment/robot-update.service b/deployment/robot-update.service
new file mode 100644
--- /dev/null
+++ b/deployment/robot-update.service
@@ -0,0 +1,9 @@
+[Unit]
+Description=Check for a Ringfour Daemon release
+Wants=network-online.target
+After=network-online.target docker.service
+
+[Service]
+Type=oneshot
+EnvironmentFile=/etc/ringfour/robot-updater.env
+ExecStart=/usr/local/lib/ringfour/robot-update.sh
diff --git a/deployment/robot-update.sh b/deployment/robot-update.sh
new file mode 100755
--- /dev/null
+++ b/deployment/robot-update.sh
@@ -0,0 +1,72 @@
+#!/bin/sh
+set -eu
+
+# Run under the robot owner's maintenance gate, which must hold the robot idle
+# through the entire update. Use the same compose project, file and env as boot.
+: "${RINGFOUR_ROBOT_COMPOSE_FILE:?Set the enrolled robot Compose file}"
+: "${RINGFOUR_ROBOT_ENV:?Set the enrolled robot Compose environment file}"
+: "${RINGFOUR_ROBOT_PROJECT:?Set the enrolled robot Compose project name}"
+: "${RINGFOUR_DAEMON_READY_URL:?Set the private Daemon readiness URL}"
+: "${RINGFOUR_ROBOT_MAINTENANCE:?Set the operator's maintenance wrapper executable}"
+
+if [ "${1:-}" != --maintenance-held ]; then
+  exec "$RINGFOUR_ROBOT_MAINTENANCE" "$0" --maintenance-held
+fi
+
+exec 9>"${RINGFOUR_ROBOT_ENV}.update.lock"
+flock -n 9 || exit 0
+
+repo=https://github.com/ringfour/ringfour-platform
+release_url=$(curl -fsSL -o /dev/null -w '%{url_effective}' "$repo/releases/latest")
+tag=${release_url##*/}
+printf '%s\n' "$tag" | grep -Eq '^main-[0-9]+-[0-9]+$' || {
+  echo "No Ringfour main release found" >&2
+  exit 1
+}
+assets="$repo/releases/download/$tag"
+
+tmp=$(mktemp -d)
+trap 'rm -rf "$tmp"' EXIT
+curl -fsSL "$assets/SHA256SUMS" -o "$tmp/SHA256SUMS"
+curl -fsSL "$assets/daemon-image.txt" -o "$tmp/daemon-image.txt"
+(
+  cd "$tmp"
+  awk '$2 == "daemon-image.txt"' SHA256SUMS > daemon.sha256
+  sha256sum -c daemon.sha256
+)
+image=$(cat "$tmp/daemon-image.txt")
+printf '%s\n' "$image" | grep -Eq '^ghcr\.io/ringfour/ringfour-platform-daemon@sha256:[a-f0-9]{64}$'
+
+compose() {
+  docker compose -p "$RINGFOUR_ROBOT_PROJECT" --env-file "$RINGFOUR_ROBOT_ENV" --env-file "$pin" \
+    -f "$RINGFOUR_ROBOT_COMPOSE_FILE" "$@"
+}
+service=${RINGFOUR_DAEMON_SERVICE:-daemon}
+pin=${RINGFOUR_ROBOT_IMAGE_PIN:-"${RINGFOUR_ROBOT_ENV}.image"}
+[ -r "$pin" ] || { echo "Initialize the robot image pin at $pin" >&2; exit 1; }
+id=$(compose ps -a -q "$service")
+[ -n "$id" ] || { echo "Start the enrolled Daemon before enabling automatic updates" >&2; exit 1; }
+previous=$(docker inspect --format '{{.Config.Image}}' "$id")
+if [ "$previous" = "$image" ] && grep -Fxq "RINGFOUR_DAEMON_IMAGE=$image" "$pin" &&
+    curl -fsS --max-time 5 "$RINGFOUR_DAEMON_READY_URL" >/dev/null; then
+  exit 0
+fi
+# A mutable previous reference cannot support a repeatable rollback.
+printf '%s\n' "$previous" | grep -Eq '@sha256:[a-f0-9]{64}$' || {
+  echo "Set an initial digest-pinned Daemon image before enabling updates" >&2
+  exit 1
+}
+export RINGFOUR_DAEMON_IMAGE="$image"
+resolved=$(compose config --format json | jq -r --arg service "$service" '.services[$service].image')
+[ "$resolved" = "$image" ] || { echo 'Compose must use RINGFOUR_DAEMON_IMAGE for the Daemon service' >&2; exit 1; }
+compose pull "$service"
+if compose up -d --no-deps --no-build "$service" &&
+    curl -fsS --max-time 5 --retry 15 --retry-delay 2 --retry-all-errors "$RINGFOUR_DAEMON_READY_URL" >/dev/null; then
+  pending_pin=$(mktemp "${pin}.XXXXXX")
+  printf 'RINGFOUR_DAEMON_IMAGE=%s\n' "$image" > "$pending_pin"
+  mv -f "$pending_pin" "$pin"
+  printf 'Updated Daemon to %s\n' "$image"
+else
+  echo "Daemon failed readiness. The last successful image pin is in $pin. Check SQLite migration compatibility before rollback." >&2
+  exit 1
+fi
diff --git a/deployment/robot-update.timer b/deployment/robot-update.timer
new file mode 100644
--- /dev/null
+++ b/deployment/robot-update.timer
@@ -0,0 +1,10 @@
+[Unit]
+Description=Periodically check for a Ringfour Daemon release
+
+[Timer]
+OnBootSec=5min
+OnUnitInactiveSec=15min
+RandomizedDelaySec=2min
+
+[Install]
+WantedBy=timers.target
diff --git a/README.md b/README.md
--- a/README.md
+++ b/README.md
@@ -31,3 +31,6 @@
 - Use [local registry setup](docs/local-registry.md) for
   development image publication.
 - Run the [component checks](docs/development.md#component-checks) for the affected application.
+
+See [delivery from main](deployment/README.md) for CLI installation, robot updates
+and N100 deployment.
diff --git a/scripts/package-cli-release.py b/scripts/package-cli-release.py
new file mode 100644
--- /dev/null
+++ b/scripts/package-cli-release.py
@@ -0,0 +1,32 @@
+"""Set the build version and public defaults for a main-channel CLI wheel."""
+import json
+import os
+from pathlib import Path
+from urllib.parse import urlsplit
+
+package = Path("apps/cli/src/ringfour_cli")
+defaults = {
+    "api_url": os.environ["RINGFOUR_API_URL"],
+    "auth0_domain": os.environ["AUTH0_DOMAIN"],
+    "auth0_client_id": os.environ["AUTH0_CLI_CLIENT_ID"],
+    "auth0_audience": os.environ["AUTH0_AUDIENCE"],
+}
+api = urlsplit(defaults["api_url"])
+domain = urlsplit("https://" + defaults["auth0_domain"])
+if (
+    not all(defaults.values())
+    or not os.environ["AUTH0_WEB_CLIENT_ID"]
+    or api.scheme != "https"
+    or not api.hostname
+    or api.username or api.password or api.query or api.fragment
+    or domain.netloc != domain.hostname
+    or domain.path or domain.query or domain.fragment
+):
+    raise SystemExit("Configure the public Auth0 and HTTPS API Actions variables.")
+
+version_file = package / "__init__.py"
+version = version_file.read_text().split('"')[1]
+run = int(os.environ["GITHUB_RUN_NUMBER"])
+attempt = int(os.environ["GITHUB_RUN_ATTEMPT"])
+version_file.write_text(f'__version__ = "{version}.dev{run * 10000 + attempt}"\n')
+(package / "release_defaults.json").write_text(json.dumps(defaults) + "\n")
