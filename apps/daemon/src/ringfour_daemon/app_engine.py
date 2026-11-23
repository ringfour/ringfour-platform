"""Pinned Docker REST boundary and durable owner-scoped workload lifecycle."""

import asyncio
import base64
import fcntl
import io
import json
import logging
import os
import re
import sqlite3
import struct
import tarfile
import threading
from contextlib import closing
from datetime import UTC, datetime
from functools import wraps
from urllib.parse import quote

import httpx

from .agent_inventory import utc_now
from .app_protocol import Platform, ownership_labels
from .db import connect
from .enrollment import load_identity

API = "/v1.46"
ACTIVE = ("accepted", "starting", "running")


class EngineError(ValueError):
    def __init__(self, code="engine_unavailable"):
        self.code = code
        super().__init__(code)


class Engine:
    def __init__(self, settings, transport=None):
        self.settings = settings
        self.client = httpx.Client(
            base_url="http://engine",
            timeout=120,
            transport=transport or httpx.HTTPTransport(uds=settings.engine_socket),
        )

    def close(self):
        self.client.close()

    def request(self, method, path, *, missing=False, **kwargs):
        try:
            response = self.client.request(method, API + path, **kwargs)
            if missing and response.status_code == 404:
                return None
            response.raise_for_status()
            return response
        except (httpx.HTTPError, ValueError) as error:
            raise EngineError() from error

    def platform(self):
        try:
            value = self.request("GET", "/info", timeout=2).json()
            arch = {"x86_64": "amd64", "aarch64": "arm64"}.get(
                value["Architecture"], value["Architecture"]
            )
            return Platform(
                os=value["OSType"],
                architecture=arch,
                native_facts=list(self.settings.engine_native_facts),
            )
        except (KeyError, ValueError) as error:
            raise EngineError() from error

    def image(self, reference):
        response = self.request(
            "GET", "/images/" + quote(reference, safe="") + "/json", missing=True
        )
        return None if response is None else response.json()

    def pull(self, target, authorization):
        reference = target["image"]
        if not re.fullmatch(r"[^\s@]+@sha256:[0-9a-f]{64}", reference):
            raise EngineError("revision_unavailable")
        registry_auth = (
            {}
            if authorization["kind"] == "anonymous"
            else {"registrytoken": authorization["token"]}
        )
        auth = base64.urlsafe_b64encode(json.dumps(registry_auth).encode()).decode()
        try:
            with self.client.stream(
                "POST",
                API + "/images/create",
                params={
                    "fromImage": reference,
                    "platform": target["os"] + "/" + target["architecture"],
                },
                headers={"X-Registry-Auth": auth},
            ) as response:
                response.raise_for_status()
                for line in response.iter_lines():
                    if line and (
                        "error" in json.loads(line) or "errorDetail" in json.loads(line)
                    ):
                        raise EngineError("pull_failed")
        except (httpx.HTTPError, ValueError) as error:
            raise EngineError("pull_failed") from error
        image = self.image(reference)
        if (
            not image
            or reference not in image.get("RepoDigests", [])
            or (image["Os"], image["Architecture"])
            != (target["os"], target["architecture"])
        ):
            raise EngineError("revision_unavailable")
        return image

    def container(self, identifier):
        response = self.request(
            "GET", "/containers/" + quote(identifier, safe="") + "/json", missing=True
        )
        return None if response is None else response.json()

    def owned(self, robot, namespace):
        filters = {
            "label": [
                f"eu.ringfour.robot-id={robot}",
                f"eu.ringfour.deployment-namespace={namespace}",
            ]
        }
        return self.request(
            "GET",
            "/containers/json",
            params={"all": "true", "filters": json.dumps(filters)},
        ).json()

    def network(self):
        name = self.settings.app_network
        if not name or name in ("host", "none"):
            raise EngineError("native_requirement_unavailable")
        value = self.request("GET", "/networks/" + quote(name, safe="")).json()
        if value.get("Driver") in ("host", "null"):
            raise EngineError("native_requirement_unavailable")
        return value["Id"]

    def output(self, identifier):
        response = self.request(
            "GET",
            "/containers/" + identifier + "/logs",
            params={"stdout": "true", "stderr": "true", "tail": "all"},
        )
        streams = {1: bytearray(), 2: bytearray()}
        raw = response.content
        while raw:
            if len(raw) < 8:
                raise EngineError("execution_failed")
            stream, length = raw[0], struct.unpack(">I", raw[4:8])[0]
            if stream not in streams or len(raw) < 8 + length:
                raise EngineError("execution_failed")
            streams[stream].extend(raw[8 : 8 + length])
            raw = raw[8 + length :]
        return bytes(streams[1]), bytes(streams[2])


def serialized(action):
    @wraps(action)
    def call(self, *args, **kwargs):
        with self.mutex:
            return action(self, *args, **kwargs)

    return call


class Executor:
    def __init__(self, settings, engine=None):
        self.settings = settings
        self.engine = engine or Engine(settings)
        self.lock = None
        self.identity = None
        self.mutex = threading.RLock()

    def open(self):
        self.identity = load_identity(self.settings.db_path)
        if not self.identity or not self.identity.get("deployment_namespace"):
            raise EngineError("ownership_conflict")
        path = self.settings.db_path + ".apps.lock"
        descriptor = os.open(path, os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
        try:
            fcntl.flock(descriptor, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            os.close(descriptor)
            raise EngineError("ownership_conflict") from None
        self.lock = descriptor
        return self

    def close(self):
        if self.lock is not None:
            os.close(self.lock)
            self.lock = None
        self.engine.close()

    def fence(self):
        if self.lock is None or load_identity(self.settings.db_path) != self.identity:
            raise EngineError("ownership_conflict")

    def row(self, run_id):
        self.fence()
        with closing(connect(self.settings.db_path)) as conn:
            conn.row_factory = sqlite3.Row
            row = conn.execute(
                "SELECT * FROM app_runs WHERE run_id=?", (run_id,)
            ).fetchone()
            intent = conn.execute(
                "SELECT * FROM app_engine_intents WHERE run_id=?", (run_id,)
            ).fetchone()
        if not row or row["robot_id"] != self.identity["robot_id"]:
            raise EngineError("ownership_conflict")
        return dict(row), None if intent is None else dict(intent)

    def labels(self, row):
        return ownership_labels(
            row["robot_id"],
            self.identity["deployment_namespace"],
            row["installation_id"],
            row["run_id"],
        )

    def verify(self, container, row, intent):
        if any(
            container.get("Config", {}).get("Labels", {}).get(k) != v
            for k, v in self.labels(row).items()
        ):
            raise EngineError("ownership_conflict")
        networks = container.get("NetworkSettings", {}).get("Networks", {}).values()
        attached = {n["NetworkID"] for n in networks if n.get("NetworkID")}
        created = container["State"]["Status"] == "created"
        if (
            container.get("Image") != intent["image_id"]
            or container.get("HostConfig", {}).get("NetworkMode")
            != intent["network_id"]
            or (attached != {intent["network_id"]} and not (created and not attached))
        ):
            raise EngineError("ownership_conflict")
        if row["container_id"] is not None and row["container_id"] != container["Id"]:
            raise EngineError("ownership_conflict")

    def phase(self, run_id, phase):
        with closing(connect(self.settings.db_path)) as conn, conn:
            conn.execute(
                "UPDATE app_engine_intents SET phase=?,updated_at=? WHERE run_id=?",
                (phase, utc_now(), run_id),
            )

    def deadline(self, row):
        with closing(connect(self.settings.db_path)) as conn:
            deadline = conn.execute(
                "SELECT deadline_at FROM device_operations WHERE operation_id=?",
                (row["operation_id"],),
            ).fetchone()[0]
        if datetime.fromisoformat(deadline) <= datetime.now(UTC):
            raise EngineError("deadline_exceeded")

    @serialized
    def create(self, run_id):
        row, intent = self.row(run_id)
        if intent:
            return self.reconcile(run_id)
        if row["state"] != "accepted":
            raise EngineError("stale_installation")
        self.deadline(row)
        snapshot = json.loads(row["snapshot_json"])
        image = self.engine.image(snapshot["image"])
        if not image or snapshot["image"] not in image.get("RepoDigests", []):
            raise EngineError("revision_unavailable")
        # Image metadata must supply a plain entrypoint and a non-root UID.
        config = image["Config"]
        if not config.get("Entrypoint") or not re.fullmatch(
            r"[1-9][0-9]*(?::[0-9]+)?", config.get("User", "")
        ):
            raise EngineError("native_requirement_unavailable")
        network = self.engine.network()
        name = "rf-" + row["robot_id"] + "-" + run_id
        with closing(connect(self.settings.db_path)) as conn, conn:
            conn.execute(
                "INSERT INTO app_engine_intents VALUES (?,?,?,?,?,?,?,0,?)",
                (
                    run_id,
                    row["robot_id"],
                    self.identity["deployment_namespace"],
                    image["Id"],
                    network,
                    name,
                    "create",
                    utc_now(),
                ),
            )
        return self.reconcile(run_id)

    @serialized
    def reconcile(self, run_id):
        row, intent = self.row(run_id)
        if intent is None:
            return self.create(run_id)
        if intent["phase"] == "removed":
            return row
        matches = [
            c
            for c in self.engine.owned(
                row["robot_id"], self.identity["deployment_namespace"]
            )
            if c.get("Labels", {}).get("eu.ringfour.run-id") == run_id
        ]
        if len(matches) > 1:
            raise EngineError("ownership_conflict")
        container = self.engine.container(
            row["container_id"] or intent["container_name"]
        )
        if matches and (container is None or matches[0]["Id"] != container["Id"]):
            raise EngineError("ownership_conflict")
        if not container:
            if row["state"] not in ACTIVE:
                return row
            if intent["phase"] != "create":
                return self.interrupt(run_id)
            body = {
                "Image": intent["image_id"],
                "Labels": self.labels(row),
                "Env": [
                    "RINGFOUR_AGENT_URL=" + (self.settings.agent_uri or ""),
                    "RINGFOUR_APP_CONFIG=/app/ringfour-config.json",
                ],
                "HostConfig": {
                    "NetworkMode": intent["network_id"],
                    "ReadonlyRootfs": False,
                    "CapDrop": ["ALL"],
                    "SecurityOpt": ["no-new-privileges:true"],
                    "Memory": self.settings.app_memory_bytes,
                    "NanoCpus": self.settings.app_nano_cpus,
                    "PidsLimit": 128,
                    "AutoRemove": False,
                    "LogConfig": {
                        "Type": "json-file",
                        "Config": {"max-size": "1m", "max-file": "1"},
                    },
                },
            }
            self.fence()
            self.engine.request(
                "POST",
                "/containers/create",
                params={"name": intent["container_name"]},
                json=body,
            )
            container = self.engine.container(intent["container_name"])
        self.verify(container, row, intent)
        with closing(connect(self.settings.db_path)) as conn, conn:
            conn.execute(
                "UPDATE app_runs SET container_id=?,updated_at=? WHERE run_id=?",
                (container["Id"], utc_now(), run_id),
            )
        state = container["State"]
        if state["Status"] == "created":
            if intent["phase"] in ("start", "observed", "terminal"):
                return self.interrupt(run_id)
            self.phase(run_id, "created")
            return self.row(run_id)[0]
        if state["Status"] not in ("running", "exited", "dead"):
            raise EngineError("engine_unavailable")
        started = state.get("StartedAt")
        if not started or started.startswith("0001-"):
            return self.interrupt(run_id)
        if row["state"] not in ACTIVE:
            return row
        now = utc_now()
        stdout, stderr = self.engine.output(container["Id"])
        # Keep the durable prefix if engine retention has rotated earlier bytes.
        # A discontinuity is explicit truncation, never deletion of saved output.
        lost_stdout = not stdout.startswith(bytes(row["stdout"]))
        lost_stderr = not stderr.startswith(bytes(row["stderr"]))
        if lost_stdout:
            stdout = bytes(row["stdout"])
        if lost_stderr:
            stderr = bytes(row["stderr"])
        terminal = state["Status"] in ("exited", "dead")
        result = (
            "stopped"
            if intent["stop_requested"]
            else "succeeded"
            if state.get("ExitCode") == 0
            else "failed"
        )
        with closing(connect(self.settings.db_path)) as conn, conn:
            # SQLite permits only observed running -> terminal; preserve fast exits.
            conn.execute(
                "UPDATE app_runs SET state='running',started_at=?,observed_at=?,updated_at=? WHERE run_id=?",
                (started, now, now, run_id),
            )
            conn.execute(
                "UPDATE app_runs SET stdout=?,stderr=?,stdout_truncated=?,stderr_truncated=?,stdout_sealed=?,stderr_sealed=? WHERE run_id=?",
                (
                    stdout[:65536],
                    stderr[:65536],
                    int(row["stdout_truncated"] or lost_stdout or len(stdout) > 65536),
                    int(row["stderr_truncated"] or lost_stderr or len(stderr) > 65536),
                    int(terminal),
                    int(terminal),
                    run_id,
                ),
            )
            if terminal:
                failure = (
                    None
                    if result in ("stopped", "succeeded")
                    else json.dumps(
                        {
                            "code": "execution_failed",
                            "message": "App process failed.",
                            "details": {"retryable": False},
                        }
                    )
                )
                conn.execute(
                    "UPDATE app_runs SET state=?,ended_at=?,exit_code=?,failure_json=? WHERE run_id=?",
                    (result, state["FinishedAt"], state["ExitCode"], failure, run_id),
                )
            conn.execute(
                "UPDATE app_engine_intents SET phase=?,updated_at=? WHERE run_id=?",
                ("terminal" if terminal else "observed", now, run_id),
            )
        return self.row(run_id)[0]

    @serialized
    def start(self, run_id):
        row = self.create(run_id)
        row, intent = self.row(run_id)
        if intent["phase"] != "created" or row["state"] not in ACTIVE:
            return row
        self.deadline(row)
        container = self.engine.container(row["container_id"])
        self.verify(container, row, intent)
        snapshot = json.loads(row["snapshot_json"])
        data = json.dumps(snapshot["configuration"], separators=(",", ":")).encode()
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w") as tar:
            info = tarfile.TarInfo("ringfour-config.json")
            info.size = len(data)
            info.mode = 0o444
            tar.addfile(info, io.BytesIO(data))
        self.engine.request(
            "PUT",
            "/containers/" + row["container_id"] + "/archive",
            params={"path": "/app"},
            content=archive.getvalue(),
        )
        self.phase(
            run_id, "start"
        )  # Ambiguous HTTP replies can never cause a second start.
        self.fence()
        self.engine.request("POST", "/containers/" + row["container_id"] + "/start")
        return self.reconcile(run_id)

    def interrupt(self, run_id):
        with closing(connect(self.settings.db_path)) as conn, conn:
            failure = json.dumps(
                {
                    "code": "container_lost",
                    "message": "Container outcome is unknown; no relaunch.",
                    "details": {"retryable": False},
                }
            )
            conn.execute(
                "UPDATE app_runs SET state='interrupted',failure_json=?,observed_at=?,updated_at=? WHERE run_id=? AND state IN ('accepted','starting','running')",
                (failure, utc_now(), utc_now(), run_id),
            )
        self.phase(run_id, "terminal")
        return self.row(run_id)[0]

    @serialized
    def stop(self, run_id):
        row = self.reconcile(run_id)
        row, intent = self.row(run_id)
        if row["state"] not in ACTIVE:
            return row
        container = self.engine.container(row["container_id"])
        self.verify(container, row, intent)
        with closing(connect(self.settings.db_path)) as conn, conn:
            conn.execute(
                "UPDATE app_engine_intents SET stop_requested=1 WHERE run_id=?",
                (run_id,),
            )
        self.fence()
        self.engine.request(
            "POST", "/containers/" + row["container_id"] + "/stop", params={"t": 10}
        )
        return self.reconcile(run_id)

    @serialized
    def remove(self, run_id):
        row, intent = self.row(run_id)
        if (
            row["state"] in ACTIVE
            or not row["stdout_sealed"]
            or not row["stderr_sealed"]
        ):
            raise EngineError("stale_installation")
        container = self.engine.container(row["container_id"])
        if container:
            self.verify(container, row, intent)
            if container["State"]["Running"]:
                raise EngineError("ownership_conflict")
            self.fence()
            self.engine.request("DELETE", "/containers/" + row["container_id"])
        self.phase(run_id, "removed")


async def run_reconciliation(settings, manager):
    while True:
        executor = manager.app_executor
        if executor:
            with closing(connect(settings.db_path)) as conn:
                runs = conn.execute(
                    "SELECT run_id FROM app_engine_intents WHERE phase NOT IN ('removed','terminal')"
                ).fetchall()
            for (run_id,) in runs:
                try:
                    await asyncio.to_thread(executor.reconcile, run_id)
                except EngineError as error:
                    logging.getLogger(__name__).warning(
                        "App reconciliation unavailable: %s", error.code
                    )
        await asyncio.sleep(1)
