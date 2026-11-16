"""Read-only keyring backend for isolated real-process tests.

Production CLI code never selects this backend. A test must opt in through
PYTHON_KEYRING_BACKEND and provide a short-lived token document in the
RINGFOUR_TEST_TOKEN_JSON environment variable.
"""

import os

from keyring.backend import KeyringBackend


class EnvironmentKeyring(KeyringBackend):
    priority = 1

    def get_password(self, service: str, username: str) -> str | None:
        del service, username
        return os.environ.get("RINGFOUR_TEST_TOKEN_JSON")

    def set_password(self, service: str, username: str, password: str) -> None:
        del service, username, password
        raise RuntimeError("The isolated test keyring is read-only")

    def delete_password(self, service: str, username: str) -> None:
        del service, username
        raise RuntimeError("The isolated test keyring is read-only")
