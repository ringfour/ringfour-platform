"""Persistent TLS identity for the future LAN development listener.

The certificate is pinned through the authenticated control plane. Corrupt or
expired identities fail closed instead of silently changing the fingerprint.
"""
import fcntl
import hashlib
import os
from datetime import UTC, datetime, timedelta
from pathlib import Path

from cryptography import x509
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.x509.oid import NameOID

from .enrollment import _write_credential


def _fingerprint(pem: bytes, now: datetime) -> str:
    certificate = x509.load_pem_x509_certificate(pem)
    key = serialization.load_pem_private_key(pem, password=None)
    public = certificate.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    private_public = key.public_key().public_bytes(serialization.Encoding.DER, serialization.PublicFormat.SubjectPublicKeyInfo)
    if public != private_public or not certificate.not_valid_before_utc <= now < certificate.not_valid_after_utc:
        raise ValueError("Invalid or expired local TLS identity")
    return hashlib.sha256(certificate.public_bytes(serialization.Encoding.DER)).hexdigest()


def local_identity(path: str, *, now: datetime | None = None) -> str:
    """Create or verify one owner-readable PEM and return its certificate digest."""
    now = now or datetime.now(UTC)
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    lock = target.with_name(target.name + ".lock")
    fd = os.open(lock, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    try:
        fcntl.flock(fd, fcntl.LOCK_EX)
        if target.is_symlink():
            raise ValueError("Local TLS identity must not be a symlink")
        if target.exists():
            if target.stat().st_mode & 0o077:
                raise ValueError("Local TLS identity permissions must be owner-only")
            return _fingerprint(target.read_bytes(), now)
        key = ec.generate_private_key(ec.SECP256R1())
        name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Ringfour local daemon")])
        cert = (x509.CertificateBuilder().subject_name(name).issuer_name(name)
                .public_key(key.public_key()).serial_number(x509.random_serial_number())
                .not_valid_before(now - timedelta(minutes=5)).not_valid_after(now + timedelta(days=365 * 5))
                .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
                .sign(key, hashes.SHA256()))
        private = key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption())
        pem = private + cert.public_bytes(serialization.Encoding.PEM)
        _write_credential(str(target), pem.decode("ascii"))
        return _fingerprint(pem, now)
    finally:
        os.close(fd)
