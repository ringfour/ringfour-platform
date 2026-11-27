"""Retained pre-App certificate-pin checks against the App transport."""

import hashlib

import pytest

from ringfour_cli import development_transport as dev_sync


def test_fingerprint_mismatch_never_sends_token(monkeypatch):
    called = []

    class Socket:
        def getpeercert(self, **_):
            return b"not-the-pinned-certificate"

    class Connection:
        sock = Socket()

        def __init__(self, *_args, **_kwargs):
            pass

        def connect(self):
            pass

        def request(self, *_args, **_kwargs):
            called.append(True)

        def close(self):
            pass

    monkeypatch.setattr(dev_sync.http.client, "HTTPSConnection", Connection)
    with pytest.raises(dev_sync.LocalConnectionError, match="fingerprint"):
        dev_sync.local_request(
            "https://robot.local:8084", "a" * 64, "secret", "GET", "/v1/local/test"
        )
    assert called == []


def test_expired_certificate_never_sends_token(monkeypatch):
    from datetime import UTC, datetime, timedelta

    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import NameOID

    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "test")])
    now = datetime.now(UTC)
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - timedelta(days=3))
        .not_valid_after(now - timedelta(days=1))
        .sign(key, hashes.SHA256())
    )
    der = cert.public_bytes(serialization.Encoding.DER)
    calls = []

    class Socket:
        def getpeercert(self, **_):
            return der

    class Connection:
        sock = Socket()

        def __init__(self, *_args, **_kwargs):
            pass

        def connect(self):
            pass

        def request(self, *_args, **_kwargs):
            calls.append(True)

        def close(self):
            pass

    monkeypatch.setattr(dev_sync.http.client, "HTTPSConnection", Connection)
    with pytest.raises(dev_sync.LocalConnectionError, match="expired"):
        dev_sync.local_request(
            "https://robot.local:8084",
            hashlib.sha256(der).hexdigest(),
            "secret",
            "GET",
            "/v1/local/test",
        )
    assert calls == []
