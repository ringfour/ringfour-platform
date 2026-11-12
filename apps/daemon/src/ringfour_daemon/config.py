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
        )
