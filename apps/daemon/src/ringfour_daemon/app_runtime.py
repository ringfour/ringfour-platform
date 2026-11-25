"""Run-scoped credentials and durable output/artifact delivery without Runner."""

import asyncio
import base64
import hashlib
import hmac
import io
import json
import logging
import os
import secrets
import sqlite3
import tarfile
from contextlib import closing
from datetime import UTC, datetime, timedelta
from pathlib import Path

import httpx

from .agent_inventory import utc_now
from .app_engine import API, Engine, EngineError
from .app_protocol import ownership_labels
from .db import connect
from .device_auth import machine_headers
from .enrollment import load_identity

logger = logging.getLogger(__name__)
MAX_ARTIFACT = 16 * 1024 * 1024


def key(settings):
    path = Path(settings.db_path + ".runtime-key")
    try:
        descriptor = os.open(
            path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600
        )
    except FileExistsError:
        descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW)
        with os.fdopen(descriptor, "rb") as stream:
            value = stream.read(33)
        if len(value) != 32:
            raise ValueError("Invalid runtime key")
        return value
    value = secrets.token_bytes(32)
    with os.fdopen(descriptor, "wb") as stream:
        stream.write(value)
        stream.flush()
        os.fsync(stream.fileno())
    return value


def credential(settings, run):
    identity = load_identity(settings.db_path)
    mac = hmac.new(
        key(settings),
        (
            identity["robot_id"] + ":" + identity["deployment_namespace"] + ":" + run
        ).encode(),
        hashlib.sha256,
    ).hexdigest()
    token = "app:" + run + ":" + mac
    with closing(connect(settings.db_path)) as conn, conn:
        conn.execute(
            "INSERT INTO app_run_credentials(run_id,token_sha256,expires_at) VALUES (?,?,?) ON CONFLICT(run_id) DO NOTHING",
            (
                run,
                hashlib.sha256(token.encode()).digest(),
                (datetime.now(UTC) + timedelta(days=3650)).isoformat(),
            ),
        )
    return token


def authenticate(settings, header):
    if not header.startswith("Bearer app:"):
        return None
    token = header.removeprefix("Bearer ")
    parts = token.split(":")
    if len(parts) != 3:
        return None
    with closing(connect(settings.db_path)) as conn:
        row = conn.execute(
            "SELECT c.token_sha256,c.expires_at,c.revoked_at,r.state FROM app_run_credentials c JOIN app_runs r ON r.run_id=c.run_id WHERE c.run_id=?",
            (parts[1],),
        ).fetchone()
    if (
        not row
        or row[2] is not None
        or row[3] not in ("accepted", "starting", "running")
        or datetime.fromisoformat(row[1]) <= datetime.now(UTC)
    ):
        return None
    return (
        parts[1]
        if hmac.compare_digest(bytes(row[0]), hashlib.sha256(token.encode()).digest())
        else None
    )


def local_run(settings, run):
    with closing(connect(settings.db_path)) as conn:
        conn.row_factory = sqlite3.Row
        row = conn.execute("SELECT * FROM app_runs WHERE run_id=?", (run,)).fetchone()
    return None if row is None else dict(row)


def running(settings, run):
    row = local_run(settings, run)
    if row is None:
        return None
    if row["state"] not in ("accepted", "starting", "running"):
        return False
    if not row["container_id"]:
        return None
    engine = Engine(settings)
    try:
        container = engine.container(row["container_id"])
        identity = load_identity(settings.db_path)
        labels = ownership_labels(
            identity["robot_id"],
            identity["deployment_namespace"],
            row["installation_id"],
            run,
        )
        if not container or any(
            container.get("Config", {}).get("Labels", {}).get(k) != v
            for k, v in labels.items()
        ):
            return None
        status = container["State"]["Status"]
        return (
            True
            if status == "running"
            else False
            if status in ("exited", "dead")
            else None
        )
    except EngineError:
        return None
    finally:
        engine.close()


def environment(settings, row):
    if not settings.app_runtime_url:
        return []
    token = credential(settings, row["run_id"])
    result = ["RINGFOUR_ARTIFACT_DIR=/artifacts/files"]
    services = ("LOGBOOK", "INTERACTION", "ARTIFACT")
    try:
        snapshot = json.loads(row["snapshot_json"])
    except (KeyError, TypeError, json.JSONDecodeError):
        snapshot = {}
    if isinstance(snapshot, dict) and isinstance(snapshot.get("runtime_settings"), dict):
        services += ("SETTINGS",)
    for service in services:
        result.extend(
            [
                "RINGFOUR_" + service + "_URL=" + settings.app_runtime_url,
                "RINGFOUR_" + service + "_TOKEN=" + token,
            ]
        )
    return result


def volume(executor, row):
    name = "rf-artifacts-" + row["robot_id"] + "-" + row["run_id"]
    labels = executor.labels(row)
    response = executor.engine.request("GET", "/volumes/" + name, missing=True)
    value = None if response is None else response.json()
    if value is None:
        value = executor.engine.request(
            "POST",
            "/volumes/create",
            json={"Name": name, "Labels": labels, "Driver": "local"},
        ).json()
    if (
        any((value.get("Labels") or {}).get(k) != v for k, v in labels.items())
        or value.get("Driver") != "local"
    ):
        raise EngineError("ownership_conflict")
    return {
        "Type": "volume",
        "Source": name,
        "Target": "/artifacts",
        "VolumeOptions": {"NoCopy": True},
    }


def directory(executor, row, user):
    archive = io.BytesIO()
    uid, _, gid = user.partition(":")
    with tarfile.open(fileobj=archive, mode="w") as tar:
        info = tarfile.TarInfo("files")
        info.type = tarfile.DIRTYPE
        info.mode = 0o700
        info.uid = int(uid)
        info.gid = int(gid or uid)
        tar.addfile(info)
    executor.engine.request(
        "PUT",
        "/containers/" + row["container_id"] + "/archive",
        params={"path": "/artifacts"},
        content=archive.getvalue(),
    )


def spool_root(settings):
    return Path(settings.db_path).parent / "app-artifact-spool"


def snapshot(settings, executor, run, value):
    path = value["path"]
    if (
        not path
        or path.startswith("/")
        or "\\" in path
        or any(p in ("", ".", "..") for p in path.split("/"))
    ):
        raise ValueError("Invalid artifact path")
    with executor.mutex, closing(connect(settings.db_path)) as conn, conn:
        executor.fence()
        row, intent = executor.row(run)
        if row["state"] != "running":
            raise ValueError("Run is not running")
        existing = conn.execute(
            "SELECT artifact_id,path,size,sha256,content_type FROM app_artifact_spool WHERE artifact_id=?",
            (value["artifact_id"],),
        ).fetchone()
        if existing:
            if (
                existing[1] != path
                or existing[4] != value["content_type"]
                or not conn.execute(
                    "SELECT 1 FROM app_artifact_spool WHERE artifact_id=? AND run_id=?",
                    (value["artifact_id"], run),
                ).fetchone()
            ):
                raise FileExistsError
            return dict(
                zip(("artifact_id", "path", "size", "sha256", "content_type"), existing)
            )
        if (
            conn.execute(
                "SELECT count(*) FROM app_artifact_spool WHERE run_id=?", (run,)
            ).fetchone()[0]
            >= 16
            or conn.execute(
                "SELECT 1 FROM app_artifact_spool WHERE run_id=? AND path=?",
                (run, path),
            ).fetchone()
        ):
            raise ValueError("Artifact retention capacity/path conflict")
        container = executor.engine.container(row["container_id"])
        executor.verify(container, row, intent)
        prefix = "/artifacts/files"
        for part in path.split("/"):
            prefix += "/" + part
            response = executor.engine.request(
                "HEAD",
                "/containers/" + row["container_id"] + "/archive",
                params={"path": prefix},
            )
            stat = json.loads(
                base64.b64decode(response.headers["X-Docker-Container-Path-Stat"])
            )
            if stat.get("linkTarget"):
                raise ValueError("Artifact paths cannot traverse symlinks")
        raw = bytearray()
        with executor.engine.client.stream(
            "GET",
            API + "/containers/" + row["container_id"] + "/archive",
            params={"path": "/artifacts/files/" + path},
        ) as response:
            response.raise_for_status()
            stat = json.loads(
                base64.b64decode(response.headers["X-Docker-Container-Path-Stat"])
            )
            if stat.get("linkTarget") or stat["size"] > MAX_ARTIFACT:
                raise ValueError("Artifact must be a bounded regular file")
            for chunk in response.iter_bytes(65536):
                raw.extend(chunk)
                if len(raw) > MAX_ARTIFACT + 65536:
                    raise ValueError("Artifact exceeds byte limit")
        with tarfile.open(fileobj=io.BytesIO(raw), mode="r:") as tar:
            members = tar.getmembers()
            if (
                len(members) != 1
                or not members[0].isfile()
                or members[0].size > MAX_ARTIFACT
            ):
                raise ValueError("Artifact must be one regular file")
            data = tar.extractfile(members[0]).read(MAX_ARTIFACT + 1)
        digest = hashlib.sha256(data).hexdigest()
        root = spool_root(settings)
        root.mkdir(mode=0o700, parents=True, exist_ok=True)
        file = root / value["artifact_id"]
        temporary = root / (value["artifact_id"] + ".pending")
        with temporary.open("wb") as stream:
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        temporary.replace(file)
        descriptor = os.open(root, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
        conn.execute(
            "INSERT INTO app_artifact_spool(artifact_id,run_id,path,size,sha256,content_type) VALUES (?,?,?,?,?,?)",
            (value["artifact_id"], run, path, len(data), digest, value["content_type"]),
        )
        return value | {"size": len(data), "sha256": digest}


async def deliver_once(settings, identity):
    async with httpx.AsyncClient(
        base_url=identity["platform_url"],
        headers=machine_headers(settings, identity),
        timeout=30,
    ) as client:
        with closing(connect(settings.db_path)) as conn:
            conn.row_factory = sqlite3.Row
            conn.execute(
                "UPDATE app_run_credentials SET revoked_at=? WHERE revoked_at IS NULL AND run_id IN (SELECT run_id FROM app_runs WHERE state NOT IN ('accepted','starting','running'))",
                (utc_now(),),
            )
            conn.execute(
                "UPDATE app_runs SET stdout_sealed=1,stderr_sealed=1 WHERE state='failed' AND started_at IS NULL"
            )
            conn.commit()
            runs = [dict(r) for r in conn.execute("SELECT * FROM app_runs")]
            artifacts = [
                dict(r)
                for r in conn.execute(
                    "SELECT * FROM app_artifact_spool WHERE delivered=0"
                )
            ]
        for row in runs:
            run = row["run_id"]
            with closing(connect(settings.db_path)) as conn, conn:
                conn.execute(
                    "INSERT INTO app_output_delivery(run_id) VALUES (?) ON CONFLICT DO NOTHING",
                    (run,),
                )
                saved = conn.execute(
                    "SELECT stdout_offset,stderr_offset,stdout_sealed,stderr_sealed,stdout_truncated,stderr_truncated FROM app_output_delivery WHERE run_id=?",
                    (run,),
                ).fetchone()
            for index, stream in enumerate(("stdout", "stderr")):
                at = saved[index]
                data = bytes(row[stream])
                sealed = row[stream + "_sealed"]
                if (
                    len(data) == at
                    and saved[index + 2] == sealed
                    and saved[index + 4] == row[stream + "_truncated"]
                ):
                    continue
                while True:
                    chunk = data[at : at + 16384]
                    final = at + len(chunk) == len(data)
                    response = await client.put(
                        f"/v1/device/app-runs/{run}/output/{stream}",
                        params={
                            "offset": at,
                            "sealed_output": bool(sealed and final),
                            "truncated": bool(row[stream + "_truncated"]),
                        },
                        content=chunk,
                    )
                    response.raise_for_status()
                    at += len(chunk)
                    with closing(connect(settings.db_path)) as conn, conn:
                        conn.execute(
                            f"UPDATE app_output_delivery SET {stream}_offset=?,{stream}_sealed=?,{stream}_truncated=? WHERE run_id=?",
                            (
                                at,
                                int(sealed and final),
                                row[stream + "_truncated"],
                                run,
                            ),
                        )
                    if final:
                        break
        for artifact in artifacts:
            path = spool_root(settings) / artifact["artifact_id"]
            data = await asyncio.to_thread(path.read_bytes)
            if (
                len(data) != artifact["size"]
                or hashlib.sha256(data).hexdigest() != artifact["sha256"]
            ):
                raise ValueError("Durable artifact digest mismatch")
            response = await client.put(
                f"/v1/device/app-runs/{artifact['run_id']}/artifacts/{artifact['artifact_id']}",
                params={
                    "path": artifact["path"],
                    "sha256": artifact["sha256"],
                    "content_type": artifact["content_type"],
                },
                content=data,
            )
            response.raise_for_status()
            with closing(connect(settings.db_path)) as conn, conn:
                conn.execute(
                    "UPDATE app_artifact_spool SET delivered=1 WHERE artifact_id=?",
                    (artifact["artifact_id"],),
                )
            path.unlink(missing_ok=True)


async def deliver(settings):
    while True:
        if settings.apps_enabled:
            try:
                identity = load_identity(settings.db_path)
                if identity:
                    await deliver_once(settings, identity)
            except (httpx.HTTPError, sqlite3.Error, OSError, ValueError):
                logger.warning(
                    "App runtime delivery pending; preserving durable evidence."
                )
        await asyncio.sleep(1)
