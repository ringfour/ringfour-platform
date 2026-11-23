const apps = [
  [
    "Site Mapping",
    "Build a coverage map of the site, zone by zone.",
    "Field Rover 01",
    "Mobile base with lidar",
    "Site zone",
    ["North hall", "West aisle", "Loading bay"],
  ],
  [
    "Visual Inspection",
    "Review camera findings and approve an aisle inspection.",
    "Inspection Arm 02",
    "Articulated arm with inspection camera",
    "Inspection checklist",
    ["Aisle clearance", "Dock safety"],
  ],
  [
    "Asset Inventory",
    "Locate missing equipment against the expected inventory.",
    "Survey Scout 03",
    "Mobile scanner with asset tag reader",
    "Inventory area",
    ["West aisle", "Loading bay"],
  ],
];
const state = [
  null,
  { robot: 1, setting: "Aisle clearance", ready: true },
  null,
];
let decision = "",
  zone = 0,
  missing = "",
  checking = true;
const link = (url, label, primary = false) =>
  '<a class="action ' +
  (primary ? "primary" : "") +
  '" href="#' +
  url +
  '">' +
  label +
  "</a>";
const drawing = (i, z = 0) =>
  '<div class="visual"><svg role="img" aria-label="' +
  [
    "Site floor plan",
    "Sample inspection camera frame of west aisle",
    "Zone inventory plan",
  ][i] +
  '" viewBox="0 0 300 150">' +
  (i === 1
    ? '<path fill="#354246" d="M0 0h300v150H0z"/><path fill="#606867" d="m0 150 119-92h62l119 92"/><path stroke="#a7af9e" stroke-width="5" fill="none" d="M0 28l115 30v24L0 100m300-72L185 58v24l115 18"/><path fill="#b98e55" d="m178 92 29-3 7 19-34 3z"/><rect x="173" y="84" width="46" height="33" stroke="#ffad51" fill="none"/><text x="10" y="140" fill="white" font-size="10">CAM 02 / WEST AISLE</text>'
    : apps[0][5]
        .map(
          (n, k) =>
            '<rect x="' +
            (12 + k * 94) +
            '" y="' +
            (28 + (k % 2) * 18) +
            '" width="88" height="76" fill="' +
            (k === z ? "#315e56" : "#242e30") +
            '" stroke="#889b96"/><text x="' +
            (19 + k * 94) +
            '" y="75" fill="#eee" font-size="12">' +
            n +
            "</text>",
        )
        .join("")) +
  "</svg></div>";
function render() {
  let [page, id] = location.hash.slice(1).split("/");
  page = page || "launcher";
  let i = Number(id) || 0,
    a = apps[i],
    s = state[i];
  if (!a) return location.replace("#catalog");
  crumb.textContent =
    {
      catalog: "Catalog",
      launcher: "Launcher",
      detail: "Catalog",
      setup: "Launcher",
      app: "Launcher",
    }[page] || "Launcher";
  document
    .querySelectorAll("nav a")
    .forEach((x) =>
      x.classList.toggle("active", x.textContent === crumb.textContent),
    );
  let html = "";
  if (["catalog", "launcher"].includes(page)) {
    let cat = page === "catalog";
    html =
      "<h1>" +
      crumb.textContent +
      '</h1><div class="grid">' +
      apps
        .map((a, i) => {
          let s = state[i];
          if (!cat && !s) return "";
          return (
            '<section class="panel">' +
            drawing(i) +
            "<h2>" +
            a[0] +
            "</h2><p>" +
            a[1] +
            "</p><p><small>" +
            (cat ? a[3] : s.ready ? "Ready to open · " + a[2] : "Needs setup") +
            "</small></p>" +
            link(
              (cat ? "detail" : s.ready ? "app" : "setup") + "/" + i,
              cat ? "View app →" : s.ready ? "Open app" : "Set up",
              !cat,
            ) +
            (s?.ready && !cat ? link("setup/" + i, "Settings") : "") +
            "</section>"
          );
        })
        .join("") +
      "</div>" +
      (!cat ? link("catalog", "Browse catalog →") : "") +
      '<footer>Studio <a href="/runs">Runs ↗</a><a href="/timeline">Timeline ↗</a><a href="/settings/workspace">People ↗</a></footer>';
  } else if (page === "detail") {
    html =
      link("catalog", "← Catalog") +
      "<h1>" +
      a[0] +
      "</h1><p>" +
      a[1] +
      "</p>" +
      drawing(i) +
      "<h2>Robot requirements</h2><p>" +
      a[3] +
      " · " +
      a[2] +
      "</p>" +
      (s
        ? "<p>Added to workspace</p>" +
          link("launcher", "Open in launcher", true)
        : '<button class="primary" id="add">Add to workspace</button>');
  } else if (page === "setup" && s) {
    html =
      link("launcher", "← Launcher") +
      "<h1>Set up " +
      a[0] +
      '</h1><section class="panel"><label>' +
      a[4] +
      ' <select id="setting">' +
      a[5]
        .map(
          (n) =>
            "<option " +
            (n === s.setting ? "selected" : "") +
            ">" +
            n +
            "</option>",
        )
        .join("") +
      '</select></label><label>Target robot <select id="robot"><option value="">Choose robot</option>' +
      apps
        .map(
          (b, k) =>
            '<option value="' +
            k +
            '" ' +
            (k !== i ? "disabled" : s.robot === i ? "selected" : "") +
            ">" +
            b[2] +
            (k !== i ? " — requires " + a[3] : "") +
            "</option>",
        )
        .join("") +
      '</select></label><button id="save">Save settings</button><button class="primary" id="deploy">Simulate deployment</button><p role="status" id="notice"></p></section>';
  } else if (page === "app" && s?.ready) {
    html =
      link("launcher", "← Launcher") +
      "<h1>" +
      a[0] +
      "</h1><p>" +
      a[2] +
      " · " +
      s.setting +
      "</p>";
    if (i === 0)
      html +=
        drawing(i, zone) +
        a[5]
          .map(
            (n, k) =>
              '<button data-zone="' +
              k +
              '" aria-pressed="' +
              (zone === k) +
              '">' +
              n +
              "</button>",
          )
          .join("") +
        "<h2>" +
        a[5][zone] +
        "</h2><p>" +
        [
          "Covered · 12 survey points",
          "Partially covered · 8 of 12 points",
          "Uncovered · Survey pending",
        ][zone] +
        '</p><button id="preview">Preview map</button>';
    if (i === 1)
      html +=
        '<div class="grid"><section class="panel">' +
        drawing(i) +
        '<h2>West aisle camera</h2><p>Finding · Crate near the marked route</p></section><section class="panel"><h2>' +
        s.setting +
        '</h2><p>✓ Camera alignment verified</p><label><input id="check" type="checkbox" ' +
        (checking ? "checked" : "") +
        "> Review aisle clearance</label><h3>Approve west aisle inspection?</h3>" +
        (decision
          ? '<p role="status">' + decision + "</p>"
          : '<button data-answer="Approved" class="primary">Approve</button><button data-answer="Reinspection requested">Request reinspection</button>') +
        "</section></div>";
    if (i === 2) {
      const assets =
        zone === 1
          ? ["Dock monitor", "Pallet scale", "Hand scanner"]
          : ["Tool cabinet", "Safety beacon", "Inspection cart"];
      html +=
        a[5]
          .map(
            (n, k) =>
              '<button data-zone="' +
              k +
              '" aria-pressed="' +
              (zone === k) +
              '">' +
              n +
              "</button>",
          )
          .join("") +
        '<section class="panel"><h2>' +
        a[5][zone] +
        "</h2><p>3 expected · 2 found · 1 missing</p>" +
        assets
          .map(
            (n, k) =>
              "<p>" +
              n +
              " · " +
              (k === 2
                ? '<button id="locate">Missing · Locate →</button>'
                : "Found") +
              "</p>",
          )
          .join("") +
        (missing
          ? '<p role="status">Last recorded location · ' +
            a[5][zone] +
            ", rack " +
            (zone + 3) +
            "</p>"
          : "") +
        "</section>";
    }
  } else return location.replace("#launcher");
  view.innerHTML = html;
  const on = (id, fn) =>
    document.getElementById(id)?.addEventListener("click", fn);
  on("add", () => {
    state[i] = { ready: false };
    render();
  });
  for (const [id, ready] of [
    ["save", false],
    ["deploy", true],
  ])
    on(id, () => {
      if (robot.value === "") {
        notice.textContent = "Choose a compatible robot.";
        return;
      }
      Object.assign(s, { robot: i, setting: setting.value, ready });
      if (ready) location.hash = "launcher";
      else notice.textContent = "Settings saved.";
    });
  view.querySelectorAll("[data-zone]").forEach(
    (b) =>
      (b.onclick = () => {
        zone = +b.dataset.zone;
        missing = "";
        render();
      }),
  );
  view.querySelectorAll("[data-answer]").forEach(
    (b) =>
      (b.onclick = () => {
        decision = b.dataset.answer;
        render();
      }),
  );
  on("check", () => (checking = !checking));
  on("preview", () => {
    content.innerHTML =
      "<h2>Sample map · " + a[5][zone] + "</h2>" + drawing(0, zone);
    result.showModal();
  });
  on("locate", () => {
    missing = true;
    render();
  });
}
window.onhashchange = () => {
  let [, id] = location.hash.slice(1).split("/");
  zone = Math.max(0, apps[id]?.[5].indexOf(state[id]?.setting) ?? 0);
  missing = "";
  render();
};
render();
