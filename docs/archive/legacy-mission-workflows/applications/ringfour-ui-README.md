# ringfour-ui

Optional helpers for mission-authored Gradio interfaces. The package provides
the Ringfour theme and an occupancy-map renderer; layouts, controls, and
mission behavior remain ordinary Gradio and mission code.

```python
import threading

import gradio as gr
import ringfour_ui as ui

class MissionState:
    def __init__(self):
        self._lock = threading.Lock()
        self._frame = None

    def publish(self, frame):
        with self._lock:
            self._frame = frame

    def snapshot(self):
        with self._lock:
            return self._frame

state = MissionState()
view = ui.MapView()
# A mission-owned worker periodically calls:
# state.publish(view.render(grid=robot.get_map(), poses=robot.get_pose()))

with gr.Blocks(title="Map viewer") as app:
    image = gr.Image(label="Map", type="numpy", interactive=False)
    gr.Timer(0.5).tick(state.snapshot, outputs=image)

ui.launch(app)  # loopback only; no public Gradio share link
```

For a real mission, keep robot work in a mission-owned loop that continues
without a browser. Browser callbacks should read copied mission state or call
mission-owned methods; browser disconnects must not stop or restart the mission.
The package does not provide execution registration, transport, authorization,
or a Console entry point. Those are separate Milestone 10 work.

`launch()` also accepts Runner-assigned `RINGFOUR_UI_PORT` and
`RINGFOUR_UI_ROOT_PATH` environment variables. The port must be a decimal TCP
port from 1 to 65535; the root path must be an absolute URL path containing
only letters, digits, `/`, `_` and `-`, with no traversal segments. Explicit
`server_port` and `root_path` arguments take precedence. These settings only
configure Gradio; they do not register the UI or make it reachable remotely.

The Gradio integration is pinned to **6.32.0** for this demo slice. Its
browser assets, Timer callbacks, queue requests and server-sent event stream
were verified in the local Base101 mission. This local test used the Runner's
loopback-only host port mapping; authenticated remote access is separate
Milestone 10 work. `launch()` defaults to loopback, disables public sharing
and run history. If a Runner supplies the optional port and root-path
environment variables, `launch()` uses them; explicit launch arguments take
precedence. The Runner assignment and authenticated gateway are not yet
implemented.

The map renderer uses the current `ringfour` spatial types and occupancy grid.
Its offline pixel tests need no robot or browser:

```sh
cd packages/ringfour-ui
uv sync --python 3.13 --extra dev
uv run --python 3.13 pytest tests -q
```

The vendored Space Grotesk font files and OFL license are under
`src/ringfour_ui/assets/fonts`. CSS embeds these files locally and does not
fetch fonts from a third-party CDN.
