"""Credential headers for authenticated device requests to the Platform."""
from pathlib import Path


def machine_headers(settings, identity):
    return {"x-ringfour-credential-id": identity["credential_id"],
            "x-ringfour-credential-secret": Path(settings.credential_path).read_text().strip()}
