import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    data_dir: str
    stage_dir: str = "/staged"
    auth_token: str | None = None

    @staticmethod
    def from_env() -> "Settings":
        return Settings(
            data_dir=os.environ.get("RUNNER_DATA_DIR", "/data"),
            stage_dir=os.environ.get("RUNNER_STAGE_DIR", "/staged"),
            auth_token=os.environ.get("RUNNER_AUTH_TOKEN"),
        )
