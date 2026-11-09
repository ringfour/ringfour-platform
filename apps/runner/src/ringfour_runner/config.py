import os
from dataclasses import dataclass


@dataclass(frozen=True)
class Settings:
    data_dir: str

    @staticmethod
    def from_env() -> "Settings":
        return Settings(data_dir=os.environ.get("RUNNER_DATA_DIR", "/data"))
