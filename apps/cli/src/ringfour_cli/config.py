"""CLI configuration: non-secret settings in a config file, environment wins."""

import json
import os
from dataclasses import dataclass
from pathlib import Path

DEFAULT_API_URL = "http://localhost:8080"
DEFAULT_AUDIENCE = "https://api.ringfour.eu"


class ConfigError(Exception):
    pass


@dataclass(frozen=True)
class Settings:
    api_url: str
    auth0_domain: str | None
    auth0_client_id: str | None
    auth0_audience: str
    config_path: Path
    config: dict


def config_path() -> Path:
    override = os.environ.get("RINGFOUR_CONFIG")
    if override:
        return Path(override)
    base = Path(os.environ.get("XDG_CONFIG_HOME", Path.home() / ".config"))
    return base / "ringfour" / "config.json"


def _pick(env_var: str, config: dict, key: str, default: str | None = None) -> str | None:
    return os.environ.get(env_var) or config.get(key) or default


def load_settings(path: Path | None = None) -> Settings:
    path = path or config_path()
    config: dict = {}
    if path.exists():
        config = json.loads(path.read_text())
    return Settings(
        api_url=_pick("RINGFOUR_API_URL", config, "api_url", DEFAULT_API_URL),
        auth0_domain=_pick("RINGFOUR_AUTH0_DOMAIN", config, "auth0_domain"),
        auth0_client_id=_pick("RINGFOUR_AUTH0_CLIENT_ID", config, "auth0_client_id"),
        auth0_audience=_pick("RINGFOUR_AUTH0_AUDIENCE", config, "auth0_audience", DEFAULT_AUDIENCE),
        config_path=path,
        config=config,
    )


def require_auth0(settings: Settings) -> None:
    missing = []
    if not settings.auth0_domain:
        missing.append("auth0_domain (or RINGFOUR_AUTH0_DOMAIN)")
    if not settings.auth0_client_id:
        missing.append("auth0_client_id (or RINGFOUR_AUTH0_CLIENT_ID)")
    if missing:
        raise ConfigError(
            "Missing Auth0 configuration: "
            + ", ".join(missing)
            + f". Set them in {settings.config_path}."
        )


def update_config(settings: Settings, **values: str) -> None:
    config = {**settings.config, **values}
    settings.config_path.parent.mkdir(parents=True, exist_ok=True)
    settings.config_path.write_text(json.dumps(config, indent=2) + "\n")


def selected_org(settings: Settings) -> str | None:
    return settings.config.get("selected_org")
