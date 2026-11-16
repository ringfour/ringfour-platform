import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    data_dir: str
    stage_dir: str = "/staged"
    auth_token: str | None = None
    logbook_url: str | None = None
    min_free_bytes: int = 268435456

    @staticmethod
    def from_env() -> "Settings":
        return Settings(
            data_dir=os.environ.get("RUNNER_DATA_DIR", "/data"),
            stage_dir=os.environ.get("RUNNER_STAGE_DIR", "/staged"),
            auth_token=os.environ.get("RUNNER_AUTH_TOKEN"),
            logbook_url=os.environ.get("RUNNER_LOGBOOK_URL") or None,
            min_free_bytes=max(0, int(os.environ.get("RUNNER_MIN_FREE_BYTES", "268435456"))),
        )
