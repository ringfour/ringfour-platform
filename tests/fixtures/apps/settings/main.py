"""Sensor-independent App exercising the packaged settings/artifact clients."""

import json
import math
import os
import signal
import threading
import uuid
from pathlib import Path

from ringfour_runtime import artifacts, logbook
from ringfour_runtime.settings import SettingsClient, SettingsRejected

stopped = threading.Event()
initialized = threading.Event()
lock = threading.Lock()
values = {}
instance = str(uuid.uuid4())


def output(event, **fields):
    print(json.dumps({"event": event, "instance": instance, **fields}), flush=True)


def apply(candidate):
    if candidate["sample_interval_ms"] == 777:
        raise SettingsRejected("The fixture deliberately rejects 777 ms.")
    gain = candidate.get("gain")
    if type(gain) not in (int, float) or not math.isfinite(gain) or not -1 <= gain <= 1:
        raise SettingsRejected("Gain must be a finite number from -1 to 1.")
    with lock:
        values.update(candidate)
    output("settings.handler", **candidate)
    initialized.set()


for signum in (signal.SIGTERM, signal.SIGINT):
    signal.signal(signum, lambda *_: stopped.set())

client = SettingsClient()
worker = threading.Thread(
    target=client.serve,
    args=(apply,),
    kwargs={
        "poll_seconds": 1,
        "stop_event": stopped,
        "on_application": lambda result: output(
            "settings.confirmed",
            application_id=result.application_id,
            outcome=result.outcome,
        ),
    },
)
worker.start()
try:
    while not initialized.wait(0.1) and not stopped.is_set():
        pass
    if not stopped.is_set():
        output(
            "settings.ready",
            startup_sample_count=os.environ.get("SAMPLE_COUNT"),
            startup_angular_speed_rad_s=os.environ.get("ANGULAR_SPEED_RAD_S"),
        )
        logbook.info(
            "Settings fixture ready",
            startup_sample_count=os.environ.get("SAMPLE_COUNT"),
            startup_angular_speed_rad_s=os.environ.get("ANGULAR_SPEED_RAD_S"),
        )
        while not stopped.wait(0.1):
            pass
finally:
    worker.join(timeout=6)
    path = Path(os.environ["RINGFOUR_ARTIFACT_DIR"]) / "reports" / "settings.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    with lock:
        path.write_text(json.dumps({"instance": instance, "values": values}))
    identifier = artifacts.publish_file(path, content_type="application/octet-stream")
    output("settings.summary", artifact_id=identifier)
