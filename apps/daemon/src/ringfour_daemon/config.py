import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    db_path: str
    runner_url: str
    platform_url: str = "http://localhost:8080"
    credential_path: str = "/data/robot-credential"

    @staticmethod
    def from_env() -> "Settings":
        return Settings(
            db_path=os.environ.get("DAEMON_DB_PATH", "/data/ringfour.db"),
            runner_url=os.environ.get("RUNNER_URL", "http://localhost:8082"),
            platform_url=os.environ.get("PLATFORM_API_URL", "http://localhost:8080"),
            credential_path=os.environ.get("DAEMON_CREDENTIAL_PATH", "/data/robot-credential"),
        )
