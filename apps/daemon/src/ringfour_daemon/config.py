import os
from dataclasses import dataclass
from urllib.parse import urlsplit


@dataclass(frozen=True)
class Settings:
    db_path: str
    platform_url: str = "http://localhost:8080"
    credential_path: str = "/data/robot-credential"
    backoff_initial_seconds: float = 1.0
    backoff_max_seconds: float = 30.0
    outbound_queue_limit: int = 128
    agent_motion_socket: str | None = None
    agent_uri: str | None = None
    engine_os: str | None = None
    engine_architecture: str | None = None
    engine_native_facts: tuple[str, ...] = ()
    app_inventory_interval_seconds: float = 15.0
    agent_inventory_timeout_seconds: float = 3.0

    development_url: str | None = None
    development_tls_path: str = "/data/app-development.pem"
    development_port: int = 8085

    apps_enabled: bool = True
    engine_socket: str = "/var/run/docker.sock"
    app_network: str | None = None
    app_runtime_url: str | None = None
    app_memory_bytes: int = 268435456
    app_nano_cpus: int = 1000000000

    def __post_init__(self):
        if self.development_url:
            origin = urlsplit(self.development_url)
            if origin.scheme != "https" or not origin.hostname or origin.username is not None or origin.password is not None or origin.path not in ("", "/") or origin.query or origin.fragment or not 1 <= self.development_port <= 65535:
                raise ValueError("Development URL must be an HTTPS origin")
        if self.app_runtime_url:
            origin = urlsplit(self.app_runtime_url)
            if origin.scheme not in ("http", "https") or not origin.hostname or origin.username is not None or origin.password is not None or origin.path not in ("", "/") or origin.query or origin.fragment or any(c in self.app_runtime_url for c in "\r\n"):
                raise ValueError("App runtime URL must be an owner-configured HTTP(S) origin")
        if self.apps_enabled and (self.app_memory_bytes < 16777216 or self.app_nano_cpus <= 0 or not self.engine_socket.startswith("/")):
            raise ValueError("App engine requires an absolute socket path and positive resource limits")

    @staticmethod
    def from_env() -> "Settings":
        return Settings(
            development_url=os.environ.get("RINGFOUR_DEVELOPMENT_URL") or None,
            development_tls_path=os.environ.get("RINGFOUR_DEVELOPMENT_TLS_PATH", "/data/app-development.pem"),
            development_port=int(os.environ.get("RINGFOUR_DEVELOPMENT_PORT", "8085")),
            apps_enabled=os.environ.get("RINGFOUR_APPS_ENABLED", "true").lower() == "true",
            engine_socket=os.environ.get("RINGFOUR_ENGINE_SOCKET", "/var/run/docker.sock"),
            app_network=os.environ.get("RINGFOUR_APP_NETWORK") or None,
            app_runtime_url=os.environ.get("RINGFOUR_APP_RUNTIME_URL", "http://daemon:8081") or None,
            app_memory_bytes=int(os.environ.get("RINGFOUR_APP_MEMORY_BYTES", "268435456")),
            app_nano_cpus=int(os.environ.get("RINGFOUR_APP_NANO_CPUS", "1000000000")),
            db_path=os.environ.get("DAEMON_DB_PATH", "/data/ringfour.db"),
            platform_url=os.environ.get("PLATFORM_API_URL", "http://localhost:8080"),
            credential_path=os.environ.get("DAEMON_CREDENTIAL_PATH", "/data/robot-credential"),
            backoff_initial_seconds=float(
                os.environ.get("DAEMON_BACKOFF_INITIAL_SECONDS", "1.0")
            ),
            backoff_max_seconds=float(os.environ.get("DAEMON_BACKOFF_MAX_SECONDS", "30.0")),
            outbound_queue_limit=int(os.environ.get("DAEMON_OUTBOUND_QUEUE_LIMIT", "128")),
            agent_motion_socket=os.environ.get("RINGFOUR_AGENT_MOTION_SOCKET") or None,
            agent_uri=os.environ.get("RINGFOUR_AGENT_URI") or None,
            engine_os=os.environ.get("RINGFOUR_ENGINE_OS") or None,
            engine_architecture=os.environ.get("RINGFOUR_ENGINE_ARCHITECTURE") or None,
            engine_native_facts=tuple(filter(None, os.environ.get("RINGFOUR_ENGINE_NATIVE_FACTS", "").split(","))),
            app_inventory_interval_seconds=float(os.environ.get("RINGFOUR_APP_INVENTORY_INTERVAL_SECONDS", "15")),
            agent_inventory_timeout_seconds=float(os.environ.get("RINGFOUR_AGENT_INVENTORY_TIMEOUT_SECONDS", "3")),
        )
