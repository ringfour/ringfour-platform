import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    db_path: str
    runner_url: str
    platform_url: str = "http://localhost:8080"
    credential_path: str = "/data/robot-credential"
    backoff_initial_seconds: float = 1.0
    backoff_max_seconds: float = 30.0
    reconcile_delay_seconds: float = 20.0
    outbound_queue_limit: int = 128
    stage_dir: str = "/staged"
    runner_auth_token: str | None = None
    local_dev_enabled: bool = False
    local_dev_host: str = "0.0.0.0"
    local_dev_port: int = 8084
    local_tls_path: str = "/data/local-dev-tls.pem"
    local_advertised_url: str | None = None
    agent_motion_socket: str | None = None
    agent_uri: str | None = None
    engine_os: str | None = None
    engine_architecture: str | None = None
    engine_native_facts: tuple[str, ...] = ()
    app_inventory_interval_seconds: float = 15.0
    agent_inventory_timeout_seconds: float = 3.0

    @staticmethod
    def from_env() -> "Settings":
        return Settings(
            db_path=os.environ.get("DAEMON_DB_PATH", "/data/ringfour.db"),
            runner_url=os.environ.get("RUNNER_URL", "http://localhost:8082"),
            platform_url=os.environ.get("PLATFORM_API_URL", "http://localhost:8080"),
            credential_path=os.environ.get("DAEMON_CREDENTIAL_PATH", "/data/robot-credential"),
            backoff_initial_seconds=float(
                os.environ.get("DAEMON_BACKOFF_INITIAL_SECONDS", "1.0")
            ),
            backoff_max_seconds=float(os.environ.get("DAEMON_BACKOFF_MAX_SECONDS", "30.0")),
            reconcile_delay_seconds=float(
                os.environ.get("DAEMON_RECONCILE_DELAY_SECONDS", "20.0")
            ),
            outbound_queue_limit=int(os.environ.get("DAEMON_OUTBOUND_QUEUE_LIMIT", "128")),
            stage_dir=os.environ.get("RUNNER_STAGE_DIR", "/staged"),
            runner_auth_token=os.environ.get("RUNNER_AUTH_TOKEN"),
            local_dev_enabled=os.environ.get("RINGFOUR_LOCAL_DEV_ENABLED", "false").lower() == "true",
            local_dev_host=os.environ.get("RINGFOUR_LOCAL_DEV_HOST", "0.0.0.0"),
            local_dev_port=int(os.environ.get("RINGFOUR_LOCAL_DEV_PORT", "8084")),
            local_tls_path=os.environ.get("RINGFOUR_LOCAL_TLS_PATH", "/data/local-dev-tls.pem"),
            local_advertised_url=os.environ.get("RINGFOUR_LOCAL_DEV_URL") or None,
            agent_motion_socket=os.environ.get("RINGFOUR_AGENT_MOTION_SOCKET") or None,
            agent_uri=os.environ.get("RINGFOUR_AGENT_URI") or None,
            engine_os=os.environ.get("RINGFOUR_ENGINE_OS") or None,
            engine_architecture=os.environ.get("RINGFOUR_ENGINE_ARCHITECTURE") or None,
            engine_native_facts=tuple(filter(None, os.environ.get("RINGFOUR_ENGINE_NATIVE_FACTS", "").split(","))),
            app_inventory_interval_seconds=float(os.environ.get("RINGFOUR_APP_INVENTORY_INTERVAL_SECONDS", "15")),
            agent_inventory_timeout_seconds=float(os.environ.get("RINGFOUR_AGENT_INVENTORY_TIMEOUT_SECONDS", "3")),
        )
