"""Build local development snapshots and connect to a pinned daemon."""

import hashlib
import hmac
import http.client
import json
import ssl
from contextlib import contextmanager
from datetime import UTC, datetime
from urllib.parse import urlsplit

from cryptography import x509


class LocalConnectionError(ValueError):
    pass


class LocalTransportError(LocalConnectionError):
    pass


def daemon_url(value):
    try:
        parsed = urlsplit(value)
        port = parsed.port or 443
        if (
            parsed.scheme != "https"
            or not parsed.hostname
            or parsed.username is not None
            or parsed.password is not None
            or parsed.query
            or parsed.fragment
            or parsed.path not in ("", "/")
            or not 1 <= port <= 65535
        ):
            raise ValueError
        return value.rstrip("/")
    except (TypeError, ValueError):
        raise ValueError(
            "Daemon URL must be HTTPS without credentials, path, query or fragment."
        ) from None


def local_request(url, certificate_sha256, token, method, path, *, body=None, key=None):
    """Pin and check certificate validity before transmitting any bearer token."""
    try:
        connection = _pinned_connection(url, certificate_sha256)
    except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
        raise LocalTransportError(
            "Could not connect to the local daemon over HTTPS."
        ) from exc
    try:
        headers = {"Authorization": f"Bearer {token}", "Accept": "application/json"}
        data = None
        if body is not None:
            data = json.dumps(body, separators=(",", ":")).encode()
            headers["Content-Type"] = "application/json"
        if key is not None:
            headers["Idempotency-Key"] = key
        connection.request(method, path, body=data, headers=headers)
        response = connection.getresponse()
        payload = response.read(2 * 1048576 + 1)
        if len(payload) > 2 * 1048576:
            raise LocalConnectionError("Daemon response exceeds its limit.")
        try:
            value = json.loads(payload)
        except (ValueError, UnicodeError):
            raise LocalConnectionError("Daemon returned an invalid response.") from None
        return response.status, value
    except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
        raise LocalTransportError(
            "Could not connect to the local daemon over HTTPS."
        ) from exc
    finally:
        connection.close()


def _pinned_connection(url, certificate_sha256):
    parsed = urlsplit(daemon_url(url))
    if (
        not isinstance(certificate_sha256, str)
        or len(certificate_sha256) != 64
        or any(c not in "0123456789abcdef" for c in certificate_sha256)
    ):
        raise LocalConnectionError("Platform returned an invalid daemon fingerprint.")
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
    context.check_hostname = False
    context.verify_mode = (
        ssl.CERT_NONE
    )  # Exact pin and validity are checked before sending credentials.
    connection = http.client.HTTPSConnection(
        parsed.hostname, parsed.port or 443, context=context, timeout=60
    )
    try:
        connection.connect()
        der = connection.sock.getpeercert(binary_form=True)
        actual = hashlib.sha256(der).hexdigest()
        if not hmac.compare_digest(actual, certificate_sha256):
            raise LocalConnectionError(
                "Daemon certificate fingerprint does not match the Platform."
            )
        cert = x509.load_der_x509_certificate(der)
        now = datetime.now(UTC)
        if not cert.not_valid_before_utc <= now < cert.not_valid_after_utc:
            raise LocalConnectionError(
                "Daemon certificate is expired or not yet valid."
            )
        return connection
    except BaseException:
        connection.close()
        raise


@contextmanager
def local_stream(url, certificate_sha256, token, method, path):
    """Yield a pinned raw HTTP response for large local transfers."""
    try:
        connection = _pinned_connection(url, certificate_sha256)
    except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
        raise LocalTransportError(
            "Could not connect to the local daemon over HTTPS."
        ) from exc
    try:
        connection.request(
            method,
            path,
            headers={"Authorization": f"Bearer {token}", "Accept-Encoding": "identity"},
        )
        response = connection.getresponse()
    except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
        connection.close()
        raise LocalTransportError(
            "Could not connect to the local daemon over HTTPS."
        ) from exc
    try:
        yield response
    finally:
        connection.close()
