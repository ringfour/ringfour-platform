const SVG_NS = "http://www.w3.org/2000/svg";
const VIEW_COPY = {
  system: ["SYSTEM LENS", "How work reaches the robot"],
  backend: ["BACKEND LENS", "Services, modules, and runtime calls"],
  contracts: ["CONTRACT LENS", "Versioned interfaces between components"],
  data: ["DATA LENS", "Persistence ownership and discovered tables"],
};
const KIND_COLORS = {
  service: "#f28a43", module: "#69a7d8", contract: "#9a8bd2", schema: "#b39de0",
  database: "#47c2b2", table: "#6fd0c3", runtime: "#d5b968", external: "#8897a8",
};

const dom = Object.fromEntries([
  "graph", "viewport", "nodes", "edges", "graphStage", "emptyState", "scanLabel", "refreshButton",
  "lensList", "graphSearch", "typeFilters", "scanStats", "viewEyebrow", "viewTitle", "detailsPanel",
  "detailsEmpty", "detailsContent", "zoomOut", "zoomIn", "fitGraph", "sourceDrawer", "sourcePath",
  "sourceCode", "closeSource", "focusStrip", "focusButtons", "clusters",
].map((id) => [id, document.getElementById(id)]));

const state = {
  graph: null,
  view: "system",
  query: "",
  disabledKinds: new Set(),
  backendFocus: null,
  selected: null,
  transform: { x: 0, y: 0, scale: 1 },
  positions: new Map(),
  dragging: null,
  lastScanSignature: null,
};

function svg(name, attributes = {}) {
  const element = document.createElementNS(SVG_NS, name);
  for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
  return element;
}

function escapeHtml(value = "") {
  return String(value).replace(/[&<>'"]/g, (character) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "'": "&#39;", '"': "&quot;" })[character]);
}

function kindColor(kind) { return KIND_COLORS[kind] || "#8897a8"; }

function visibleGraph() {
  if (!state.graph) return { nodes: [], edges: [] };
  let nodes = state.graph.nodes.filter((node) => node.views.includes(state.view) && !state.disabledKinds.has(node.kind));
  const available = new Set(nodes.map((node) => node.id));
  let edges = state.graph.edges.filter((edge) => edge.views.includes(state.view) && available.has(edge.source) && available.has(edge.target));
  if (state.view === "backend") {
    if (!state.backendFocus) {
      nodes = nodes.filter((node) => node.kind !== "module");
    } else {
      const moduleIds = new Set(nodes.filter((node) => node.kind === "module" && node.owner === state.backendFocus).map((node) => node.id));
      const included = new Set([state.backendFocus, ...moduleIds]);
      for (const edge of edges) {
        if ((moduleIds.has(edge.source) || edge.source === state.backendFocus) && edge.kind !== "contains") included.add(edge.target);
        if ((moduleIds.has(edge.target) || edge.target === state.backendFocus) && edge.kind !== "contains") included.add(edge.source);
      }
      nodes = nodes.filter((node) => included.has(node.id));
      edges = edges.filter((edge) => edge.kind !== "contains");
    }
    const focused = new Set(nodes.map((node) => node.id));
    edges = edges.filter((edge) => focused.has(edge.source) && focused.has(edge.target));
  }
  const query = state.query.trim().toLowerCase();
  if (query) {
    const direct = new Set(nodes.filter((node) => [node.label, node.kind, node.layer, node.summary, ...(node.facts || [])].join(" ").toLowerCase().includes(query)).map((node) => node.id));
    const included = new Set(direct);
    for (const edge of edges) if (direct.has(edge.source) || direct.has(edge.target)) { included.add(edge.source); included.add(edge.target); }
    nodes = nodes.filter((node) => included.has(node.id));
    const narrowed = new Set(nodes.map((node) => node.id));
    edges = edges.filter((edge) => narrowed.has(edge.source) && narrowed.has(edge.target));
  }
  return { nodes, edges };
}

function layout(nodes) {
  const width = Math.max(700, dom.graphStage.clientWidth);
  const height = Math.max(500, dom.graphStage.clientHeight);
  const positions = new Map();
  if (state.view === "system" || (state.view === "backend" && !state.backendFocus)) {
    const curated = {
      "platform-api": [.14, .2], contracts: [.5, .11], postgres: [.14, .72], daemon: [.38, .34],
      "daemon-sqlite": [.38, .76], runner: [.61, .34], "runner-sqlite": [.61, .76], mission: [.81, .34],
      sdk: [.81, .58], agent: [.93, .58], ros: [.93, .81],
    };
    for (const node of nodes) {
      const point = curated[node.id] || [.5, .5];
      positions.set(node.id, { x: 80 + point[0] * (width - 220), y: 45 + point[1] * (height - 150) });
    }
    return positions;
  }
  if (state.view === "backend" && state.backendFocus) {
    const owner = nodes.find((node) => node.id === state.backendFocus);
    const modules = nodes.filter((node) => node.kind === "module" && node.owner === state.backendFocus).sort((a, b) => a.label.localeCompare(b.label));
    const related = nodes.filter((node) => node.id !== state.backendFocus && !modules.includes(node));
    if (owner) positions.set(owner.id, { x: width / 2, y: 56 });
    const columns = Math.max(2, Math.min(4, Math.floor((width - 80) / 195)));
    const startX = width / 2 - ((columns - 1) * 195) / 2;
    modules.forEach((node, index) => positions.set(node.id, { x: startX + (index % columns) * 195, y: 165 + Math.floor(index / columns) * 84 }));
    const moduleRows = Math.ceil(modules.length / columns);
    const relatedY = 185 + moduleRows * 84;
    related.forEach((node, index) => positions.set(node.id, { x: width / 2 - ((related.length - 1) * 195) / 2 + index * 195, y: relatedY }));
    return positions;
  }
  const columns = state.view === "backend"
    ? [["service", "runtime", "external", "database"], ["module"]]
    : state.view === "contracts" ? [["contract"], ["service"], ["schema"]] : [["database", "service"], ["table"]];
  const kindColumn = new Map();
  columns.forEach((kinds, index) => kinds.forEach((kind) => kindColumn.set(kind, index)));
  const grouped = Array.from({ length: columns.length }, () => []);
  for (const node of nodes) grouped[kindColumn.get(node.kind) ?? 0].push(node);
  grouped.forEach((items, column) => {
    items.sort((a, b) => a.label.localeCompare(b.label));
    const availableHeight = Math.max(height - 110, items.length * 72);
    const x = 95 + column * ((width - 260) / Math.max(1, columns.length - 1));
    items.forEach((node, index) => positions.set(node.id, { x, y: 60 + (index + .5) * (availableHeight / Math.max(1, items.length)) }));
  });
  return positions;
}

function edgePath(start, end) {
  const sx = start.x + 84;
  const sy = start.y;
  const tx = end.x - 84;
  const ty = end.y;
  if (Math.abs(tx - sx) < 40) {
    const bend = Math.max(36, Math.abs(ty - sy) * .35);
    return `M ${start.x} ${sy + 28} C ${start.x + bend} ${sy + 50}, ${end.x + bend} ${ty - 50}, ${end.x} ${ty - 28}`;
  }
  const curve = Math.max(35, Math.abs(tx - sx) * .42);
  return `M ${sx} ${sy} C ${sx + curve} ${sy}, ${tx - curve} ${ty}, ${tx} ${ty}`;
}

function renderGraph({ fit = false } = {}) {
  const graph = visibleGraph();
  state.positions = layout(graph.nodes);
  dom.nodes.replaceChildren();
  dom.edges.replaceChildren();
  dom.clusters.replaceChildren();
  dom.emptyState.hidden = graph.nodes.length > 0;

  if (state.view === "backend" && state.backendFocus) {
    const modulePoints = graph.nodes.filter((node) => node.kind === "module" && node.owner === state.backendFocus).map((node) => state.positions.get(node.id)).filter(Boolean);
    if (modulePoints.length) {
      const minX = Math.min(...modulePoints.map((point) => point.x)) - 105;
      const maxX = Math.max(...modulePoints.map((point) => point.x)) + 105;
      const minY = Math.min(...modulePoints.map((point) => point.y)) - 52;
      const maxY = Math.max(...modulePoints.map((point) => point.y)) + 50;
      dom.clusters.append(svg("rect", { class: "cluster-body", x: minX, y: minY, width: maxX - minX, height: maxY - minY }));
      const label = svg("text", { class: "cluster-label", x: minX + 12, y: minY + 17 });
      label.textContent = `${state.graph.nodes.find((node) => node.id === state.backendFocus)?.label || "Service"} modules · ${modulePoints.length}`;
      dom.clusters.append(label);
    }
  }

  for (const item of graph.edges) {
    const start = state.positions.get(item.source);
    const end = state.positions.get(item.target);
    if (!start || !end) continue;
    const group = svg("g", { class: "edge-group", "data-edge-id": item.id, tabindex: "0" });
    const path = edgePath(start, end);
    group.append(svg("path", { class: "edge-hit", d: path }));
    group.append(svg("path", { class: "edge-line", d: path }));
    const label = svg("text", { class: "edge-label", x: (start.x + end.x) / 2, y: (start.y + end.y) / 2 - 5 });
    label.textContent = item.label;
    group.append(label);
    group.addEventListener("click", (event) => { event.stopPropagation(); selectItem("edge", item.id); });
    dom.edges.append(group);
  }

  for (const item of graph.nodes) {
    const position = state.positions.get(item.id);
    const group = svg("g", { class: "node", transform: `translate(${position.x - 84} ${position.y - 29})`, "data-node-id": item.id, tabindex: "0", role: "button", "aria-label": item.label, style: `--kind-color:${kindColor(item.kind)}` });
    group.append(svg("rect", { class: "node-body", width: 168, height: 58 }));
    group.append(svg("rect", { class: "node-rail", width: 3, height: 58 }));
    const kind = svg("text", { class: "node-kind", x: 13, y: 15 });
    kind.textContent = item.kind;
    group.append(kind);
    const title = svg("text", { class: "node-title", x: 13, y: 34 });
    title.textContent = item.label.length > 22 ? `${item.label.slice(0, 21)}…` : item.label;
    group.append(title);
    const subtitle = svg("text", { class: "node-subtitle", x: 13, y: 49 });
    subtitle.textContent = item.layer.length > 27 ? `${item.layer.slice(0, 26)}…` : item.layer;
    group.append(subtitle);
    if (item.facts?.length) {
      const count = svg("text", { class: "node-count", x: 157, y: 15 });
      count.textContent = item.facts.length;
      group.append(count);
    }
    group.addEventListener("click", (event) => { event.stopPropagation(); selectItem("node", item.id); });
    group.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") selectItem("node", item.id); });
    dom.nodes.append(group);
  }
  if (fit) fitGraph(); else applyTransform();
  refreshSelectionClasses();
}

function applyTransform() {
  const { x, y, scale } = state.transform;
  dom.viewport.setAttribute("transform", `translate(${x} ${y}) scale(${scale})`);
}

function fitGraph() {
  const graph = visibleGraph();
  if (!graph.nodes.length) return;
  const points = [...state.positions.values()];
  const minX = Math.min(...points.map((point) => point.x)) - 115;
  const maxX = Math.max(...points.map((point) => point.x)) + 115;
  const minY = Math.min(...points.map((point) => point.y)) - 70;
  const maxY = Math.max(...points.map((point) => point.y)) + 70;
  const width = dom.graphStage.clientWidth;
  const height = dom.graphStage.clientHeight;
  const scale = Math.min(1.1, Math.max(.22, Math.min(width / (maxX - minX), height / (maxY - minY)) * .92));
  state.transform = { scale, x: (width - (minX + maxX) * scale) / 2, y: (height - (minY + maxY) * scale) / 2 };
  applyTransform();
}

function refreshSelectionClasses() {
  document.querySelectorAll(".node, .edge-group").forEach((element) => element.classList.remove("active", "dimmed"));
  if (!state.selected) return;
  const related = new Set();
  if (state.selected.type === "node") {
    related.add(state.selected.id);
    for (const edge of state.graph.edges) if (edge.source === state.selected.id || edge.target === state.selected.id) { related.add(edge.source); related.add(edge.target); }
    document.querySelectorAll(".node").forEach((element) => element.classList.toggle("dimmed", !related.has(element.dataset.nodeId)));
    document.querySelectorAll(".edge-group").forEach((element) => {
      const edge = state.graph.edges.find((item) => item.id === element.dataset.edgeId);
      element.classList.toggle("dimmed", edge.source !== state.selected.id && edge.target !== state.selected.id);
    });
    document.querySelector(`[data-node-id="${CSS.escape(state.selected.id)}"]`)?.classList.add("active");
  } else {
    const edge = state.graph.edges.find((item) => item.id === state.selected.id);
    document.querySelector(`[data-edge-id="${CSS.escape(state.selected.id)}"]`)?.classList.add("active");
    document.querySelectorAll(".node").forEach((element) => element.classList.toggle("dimmed", ![edge?.source, edge?.target].includes(element.dataset.nodeId)));
  }
}

function selectItem(type, id) {
  state.selected = { type, id };
  renderDetails();
  refreshSelectionClasses();
  dom.detailsPanel.classList.add("open");
}

function renderDetails() {
  const selection = state.selected;
  if (!selection || !state.graph) {
    dom.detailsEmpty.hidden = false;
    dom.detailsContent.hidden = true;
    return;
  }
  dom.detailsEmpty.hidden = true;
  dom.detailsContent.hidden = false;
  if (selection.type === "edge") return renderEdgeDetails(state.graph.edges.find((edge) => edge.id === selection.id));
  const node = state.graph.nodes.find((item) => item.id === selection.id);
  if (!node) return;
  const relationships = state.graph.edges.filter((edge) => edge.source === node.id || edge.target === node.id);
  const relationshipHtml = relationships.map((edge) => {
    const outgoing = edge.source === node.id;
    const otherId = outgoing ? edge.target : edge.source;
    const other = state.graph.nodes.find((item) => item.id === otherId);
    return `<button class="relationship" data-select-type="node" data-select-id="${escapeHtml(otherId)}"><span><strong>${outgoing ? "→" : "←"} ${escapeHtml(other?.label || otherId)}</strong><small>${escapeHtml(edge.label)} · ${escapeHtml(edge.protocol)}</small></span><span>Inspect</span></button>`;
  }).join("");
  dom.detailsContent.innerHTML = `
    <div class="details-header">
      <span class="details-badge" style="--kind-color:${kindColor(node.kind)}">${escapeHtml(node.kind)} · ${escapeHtml(node.layer)}</span>
      <h2>${escapeHtml(node.label)}</h2><p>${escapeHtml(node.summary)}</p>
    </div>
    ${node.facts?.length ? `<section class="details-section"><p class="section-label">DISCOVERED INTERFACES · ${node.facts.length}</p><ul class="detail-list">${node.facts.slice(0, 40).map((fact) => `<li>${escapeHtml(fact)}</li>`).join("")}</ul>${node.facts.length > 40 ? `<p class="muted">Showing the first 40 items.</p>` : ""}</section>` : ""}
    <section class="details-section"><p class="section-label">RELATIONSHIPS · ${relationships.length}</p>${relationshipHtml || `<p class="muted">No relationships in the current index.</p>`}</section>
    <section class="details-section"><p class="section-label">SOURCE EVIDENCE · ${node.sources.length}</p>${sourceLinks(node.sources)}</section>`;
  bindDetailActions();
}

function renderEdgeDetails(item) {
  if (!item) return;
  const source = state.graph.nodes.find((node) => node.id === item.source);
  const target = state.graph.nodes.find((node) => node.id === item.target);
  dom.detailsContent.innerHTML = `
    <div class="details-header">
      <span class="details-badge" style="--kind-color:${kindColor(source?.kind)}">${escapeHtml(item.protocol)}</span>
      <h2>${escapeHtml(source?.label || item.source)} → ${escapeHtml(target?.label || item.target)}</h2>
      <p>${escapeHtml(item.summary)}</p>
    </div>
    <section class="details-section"><p class="section-label">CONNECTION</p><ul class="detail-list"><li>${escapeHtml(item.kind)}</li><li>${escapeHtml(item.protocol)}</li></ul></section>
    <section class="details-section"><p class="section-label">SOURCE EVIDENCE · ${item.sources.length}</p>${sourceLinks(item.sources)}</section>`;
  bindDetailActions();
}

function sourceLinks(sources = []) {
  if (!sources.length) return `<p class="muted">This high-level relationship is curated from the architecture contract.</p>`;
  return sources.map((source) => `<button class="source-link" data-source-path="${escapeHtml(source.path)}" data-source-line="${source.line}"><code>${escapeHtml(source.path)}</code><span>${escapeHtml(source.label)} · L${source.line}</span></button>`).join("");
}

function bindDetailActions() {
  dom.detailsContent.querySelectorAll("[data-select-id]").forEach((button) => button.addEventListener("click", () => selectItem(button.dataset.selectType, button.dataset.selectId)));
  dom.detailsContent.querySelectorAll("[data-source-path]").forEach((button) => button.addEventListener("click", () => showSource(button.dataset.sourcePath, Number(button.dataset.sourceLine))));
}

async function showSource(path, line) {
  dom.sourceDrawer.hidden = false;
  dom.sourcePath.textContent = `${path}:${line}`;
  dom.sourceCode.textContent = "Loading source…";
  try {
    const response = await fetch(`/api/source?path=${encodeURIComponent(path)}&line=${line}`);
    if (!response.ok) throw new Error("Source unavailable");
    const excerpt = await response.json();
    dom.sourceCode.innerHTML = excerpt.lines.map((text, index) => {
      const number = excerpt.start + index;
      return `<span class="code-line${number === excerpt.focus ? " focus" : ""}"><span class="line-number">${number}</span>${escapeHtml(text) || " "}</span>`;
    }).join("");
  } catch (error) { dom.sourceCode.textContent = error.message; }
}

function renderFilters() {
  const kinds = [...new Set(state.graph.nodes.filter((node) => node.views.includes(state.view)).map((node) => node.kind))];
  dom.typeFilters.innerHTML = kinds.map((kind) => `<button class="type-filter${state.disabledKinds.has(kind) ? " off" : ""}" type="button" data-kind="${kind}" style="--kind-color:${kindColor(kind)}">${kind}</button>`).join("");
  dom.typeFilters.querySelectorAll("button").forEach((button) => button.addEventListener("click", () => {
    state.disabledKinds.has(button.dataset.kind) ? state.disabledKinds.delete(button.dataset.kind) : state.disabledKinds.add(button.dataset.kind);
    renderFilters(); renderGraph({ fit: true });
  }));
}

function renderFocusControls() {
  dom.focusStrip.hidden = state.view !== "backend";
  if (state.view !== "backend" || !state.graph) return;
  const choices = [
    [null, "Service map"],
    ["platform-api", "Platform API"],
    ["daemon", "Daemon"],
    ["runner", "Runner"],
  ];
  dom.focusButtons.innerHTML = choices.map(([id, label]) => {
    const count = id ? state.graph.nodes.filter((node) => node.kind === "module" && node.owner === id).length : 0;
    return `<button class="focus-button${state.backendFocus === id ? " active" : ""}" type="button" data-focus="${id || "overview"}">${label}${count ? `<span class="count">${count}</span>` : ""}</button>`;
  }).join("");
  dom.focusButtons.querySelectorAll("button").forEach((button) => button.addEventListener("click", () => {
    state.backendFocus = button.dataset.focus === "overview" ? null : button.dataset.focus;
    state.selected = null;
    renderFocusControls(); renderGraph({ fit: true }); renderDetails();
  }));
}

function renderStats() {
  const meta = state.graph.meta;
  dom.scanStats.innerHTML = `<dt>Files scanned</dt><dd>${meta.filesScanned}</dd><dt>Code routes</dt><dd>${meta.routes.api + meta.routes.daemon + meta.routes.runner}</dd><dt>OpenAPI operations</dt><dd>${meta.routes.openapi}</dd><dt>Tables</dt><dd>${meta.counts.table}</dd><dt>Scan time</dt><dd>${meta.durationMs} ms</dd>`;
}

async function loadGraph({ quiet = false } = {}) {
  if (!quiet) {
    dom.refreshButton.disabled = true;
    dom.scanLabel.parentElement.className = "scan-state loading";
    dom.scanLabel.textContent = "Scanning repository…";
  }
  try {
    const response = await fetch(`/api/graph?t=${Date.now()}`);
    if (!response.ok) throw new Error("Scanner returned an error");
    const graph = await response.json();
    const signature = JSON.stringify([graph.meta.filesScanned, graph.nodes.length, graph.edges.length, graph.meta.routes, graph.meta.counts]);
    const changed = state.lastScanSignature && state.lastScanSignature !== signature;
    state.lastScanSignature = signature;
    state.graph = graph;
    renderFilters(); renderFocusControls(); renderStats(); renderGraph({ fit: !quiet || changed }); renderDetails();
    dom.scanLabel.parentElement.className = "scan-state";
    dom.scanLabel.textContent = `${graph.meta.filesScanned} files · ${graph.nodes.length} nodes · ${changed ? "changes loaded" : "live"}`;
  } catch (error) {
    dom.scanLabel.parentElement.className = "scan-state error";
    dom.scanLabel.textContent = error.message;
  } finally { dom.refreshButton.disabled = false; }
}

function changeView(view) {
  state.view = view;
  state.selected = null;
  state.disabledKinds.clear();
  state.backendFocus = null;
  dom.lensList.querySelectorAll(".lens").forEach((button) => button.classList.toggle("active", button.dataset.view === view));
  [dom.viewEyebrow.textContent, dom.viewTitle.textContent] = VIEW_COPY[view];
  renderFilters(); renderFocusControls(); renderGraph({ fit: true }); renderDetails();
}

function zoom(factor, centerX = dom.graphStage.clientWidth / 2, centerY = dom.graphStage.clientHeight / 2) {
  const oldScale = state.transform.scale;
  const scale = Math.min(2.5, Math.max(.18, oldScale * factor));
  state.transform.x = centerX - (centerX - state.transform.x) * (scale / oldScale);
  state.transform.y = centerY - (centerY - state.transform.y) * (scale / oldScale);
  state.transform.scale = scale;
  applyTransform();
}

dom.refreshButton.addEventListener("click", () => loadGraph());
dom.lensList.addEventListener("click", (event) => { const button = event.target.closest("[data-view]"); if (button) changeView(button.dataset.view); });
dom.graphSearch.addEventListener("input", () => { state.query = dom.graphSearch.value; renderGraph({ fit: true }); });
dom.zoomIn.addEventListener("click", () => zoom(1.2));
dom.zoomOut.addEventListener("click", () => zoom(1 / 1.2));
dom.fitGraph.addEventListener("click", fitGraph);
dom.closeSource.addEventListener("click", () => { dom.sourceDrawer.hidden = true; });
dom.graph.addEventListener("click", () => { state.selected = null; renderDetails(); refreshSelectionClasses(); dom.detailsPanel.classList.remove("open"); });
dom.graph.addEventListener("wheel", (event) => { event.preventDefault(); const rect = dom.graph.getBoundingClientRect(); zoom(event.deltaY < 0 ? 1.12 : 1 / 1.12, event.clientX - rect.left, event.clientY - rect.top); }, { passive: false });
dom.graph.addEventListener("pointerdown", (event) => { if (event.target.closest(".node, .edge-group")) return; state.dragging = { x: event.clientX, y: event.clientY, startX: state.transform.x, startY: state.transform.y }; dom.graph.setPointerCapture(event.pointerId); dom.graph.classList.add("dragging"); });
dom.graph.addEventListener("pointermove", (event) => { if (!state.dragging) return; state.transform.x = state.dragging.startX + event.clientX - state.dragging.x; state.transform.y = state.dragging.startY + event.clientY - state.dragging.y; applyTransform(); });
dom.graph.addEventListener("pointerup", () => { state.dragging = null; dom.graph.classList.remove("dragging"); });
window.addEventListener("resize", () => renderGraph({ fit: true }));
window.addEventListener("keydown", (event) => { if (event.key === "/" && document.activeElement !== dom.graphSearch) { event.preventDefault(); dom.graphSearch.focus(); } if (event.key === "Escape") { dom.sourceDrawer.hidden = true; } });

await loadGraph();
setInterval(() => loadGraph({ quiet: true }), 15_000);
