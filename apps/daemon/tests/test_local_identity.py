from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

from ringfour_daemon.local_identity import local_identity


def test_identity_is_persistent_private_and_concurrent(tmp_path):
    path = tmp_path / "tls" / "local.pem"
    with ThreadPoolExecutor(max_workers=4) as pool:
        values = list(pool.map(lambda _: local_identity(str(path)), range(4)))
    assert len(set(values)) == 1 and len(values[0]) == 64
    assert path.stat().st_mode & 0o777 == 0o600
    assert local_identity(str(path)) == values[0]


def test_invalid_expired_and_symlink_identities_fail_closed(tmp_path):
    path = tmp_path / "local.pem"
    start = datetime(2026, 1, 1, tzinfo=UTC)
    local_identity(str(path), now=start)
    with pytest.raises(ValueError, match="expired"):
        local_identity(str(path), now=start + timedelta(days=365 * 6))
    path.write_text("invalid")
    with pytest.raises(ValueError):
        local_identity(str(path), now=start)
    path.unlink()
    target = tmp_path / "target"
    target.write_text("keep")
    path.symlink_to(target)
    with pytest.raises(ValueError, match="symlink"):
        local_identity(str(path))
    assert Path(target).read_text() == "keep"


def test_generated_identity_works_for_actual_tls_connection(tmp_path):
    import hashlib
    import socket
    import ssl
    from concurrent.futures import ThreadPoolExecutor

    path = tmp_path / "local.pem"
    fingerprint = local_identity(str(path))
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(str(path))
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    listener.settimeout(5)

    def accept():
        raw, _ = listener.accept()
        with context.wrap_socket(raw, server_side=True) as peer:
            peer.sendall(b"ok")

    try:
        with ThreadPoolExecutor(max_workers=1) as pool:
            future = pool.submit(accept)
            client = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
            client.check_hostname = False
            client.verify_mode = ssl.CERT_NONE
            with socket.create_connection(listener.getsockname(), timeout=5) as raw, client.wrap_socket(raw, server_hostname="localhost") as peer:
                assert hashlib.sha256(peer.getpeercert(binary_form=True)).hexdigest() == fingerprint
                assert peer.recv(2) == b"ok"
            future.result(timeout=5)
    finally:
        listener.close()
