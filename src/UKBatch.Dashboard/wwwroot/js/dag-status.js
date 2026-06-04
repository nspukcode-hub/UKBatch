// UKBatch.Dashboard read-only Drawflow status canvas — ES module.
//
// Used instead of the SVG/foreignObject DagView for the live RunDetail page. Drawflow renders nodes as
// absolutely-positioned <div>s in a precanvas it pans/zooms via `transform` — there is NO
// <foreignObject>, so the Chromium/WebKit "foreignObject content displaces under ancestor
// transform: scale" defect that broke DagView under zoom cannot occur.
//
// READ-ONLY topology: editor_mode='fixed' (pan + zoom; NO node drag, NO connection edit, NO palette/drop).
// This module registers no node-move / connection-create / connection-remove / drop / drag-over handlers
// and exposes NO topology-mutation method. There are exactly TWO discrete JS→.NET callbacks, both via the
// SAME delegated container click listener (see the future-bump checklist #1 — Drawflow's own node-select
// event is DEAD in 'fixed' mode): node-select (open the inspector) and approve (the in-node
// Approve button on a pending gate, which routes to the existing REST approve path on the .NET side).
//
// Re-implements (does NOT import) the editor's UMD loader + escapeHtml + arrow marker. Coupling a
// read-only viewer to the editor's mutation module is a regression risk we refuse.
//
// (Source-grep discipline, mirror DrawflowCanvas.razor: this header deliberately does NOT write the
// quoted handler-registration literals so the strict literal scans stay precise.)
//
// ── FUTURE-BUMP CHECKLIST — re-verify ALL THREE against any new lib/drawflow/drawflow.min.js ──
//   1. 'fixed'-mode click() EARLY-RETURN: a node-card click does NOT dispatch the node-select event (it
//      return-falses when classList[0] is neither parent-drawflow nor drawflow). We therefore use a
//      DELEGATED container click listener, not Drawflow's node-select subscription.
//      If a bump makes the node-select event fire in 'fixed' mode, the delegated path still works.
//   2. CONNECTION CLASS ENCODING: addConnection tags each .connection with `node_out_node-<src>` +
//      `node_in_node-<tgt>`; `click()` decodes them as classList[2].slice(14) / classList[1].slice(13).
//      `applyEdgeKinds` reads THAT authoritative encoding to build `data-edgeid` — NOT
//      append order. If the encoding changes, edge status targeting breaks silently.
//   3. WHEEL-ZOOM is Ctrl-GATED (`zoom_enter`: `e.ctrlKey && …`). The toolbar buttons are the primary
//      zoom affordance. `zoom_reset()` restores zoom=1 but NOT pan — `resetViewImpl` also clears
//      canvas_x/canvas_y.

const _state = new WeakMap();

// Vendored Drawflow is UMD-only (no ESM dist for v0.0.60). A STATIC ESM `import` of a UMD bundle is
// unreliable across browsers (ESM strict mode: top-level `this` is undefined → the global attach is
// brittle; empirically left globalThis.Drawflow unset). The robust path is a CLASSIC <script> inject
// (top-level `this`/`self` === window → the UMD else-branch sets window.Drawflow), await onload, read
// the global. Still LAZY: runs only when a page imports THIS module + calls init(). (Same as dag-editor.js.)
let _drawflowLoad = null;
function loadDrawflow() {
    if (globalThis.Drawflow) return Promise.resolve(globalThis.Drawflow);
    if (_drawflowLoad) return _drawflowLoad;
    _drawflowLoad = new Promise((resolve, reject) => {
        const src = new URL('../lib/drawflow/drawflow.min.js', import.meta.url).href;
        const s = document.createElement('script');
        s.src = src;
        s.onload = () => globalThis.Drawflow
            ? resolve(globalThis.Drawflow)
            : reject(new Error('Drawflow script loaded but window.Drawflow is unset'));
        s.onerror = () => reject(new Error('Failed to load Drawflow from ' + src));
        document.head.appendChild(s);
    });
    return _drawflowLoad;
}

// Drawflow's vendored base CSS provides `.drawflow-node { position: absolute }` + the precanvas
// transform mechanics — WITHOUT it nodes ignore their inline left/top and stack in document flow
// (vertical column) and connection anchors collapse (no visible lines). The Editor PAGE links it
// (Editor.razor), but DagStatusCanvas is used on multiple pages (RunDetail + Detail), so this module
// injects the <link> itself, idempotently (id guard) — keeping the read-only canvas self-contained.
// Returns a Promise that resolves once the stylesheet is APPLIED. We MUST await this before adding nodes
// + connections: Drawflow computes each connection's SVG path from the node port positions at addConnection
// time. If the CSS (which supplies `.drawflow-node{position:absolute}`) is still loading, the nodes are at
// their document-flow (stacked, top-left) positions, so the connection paths freeze there and never move
// when the CSS later positions the nodes — the "arrows detached at the top of the canvas" bug.
function ensureDrawflowCss() {
    const existing = document.getElementById('ukbatch-drawflow-css');
    if (existing) {
        // Already injected (this or the editor). If it has finished loading, resolve now; else wait for it.
        if (existing.sheet) return Promise.resolve();
        return new Promise(res => existing.addEventListener('load', () => res(), { once: true }));
    }
    return new Promise(res => {
        const href = new URL('../lib/drawflow/drawflow.min.css', import.meta.url).href;
        const link = document.createElement('link');
        link.id = 'ukbatch-drawflow-css';
        link.rel = 'stylesheet';
        link.href = href;
        link.addEventListener('load', () => res(), { once: true });
        link.addEventListener('error', () => res(), { once: true }); // never block render on a CSS 404
        document.head.appendChild(link);
    });
}

// One-time SVG <marker> def for the n8n-style connection arrowheads (idempotent — id guard). Shared
// with the editor's `#ukbatch-arrow` def; whichever canvas mounts first creates it.
function ensureArrowMarker() {
    if (document.getElementById('ukbatch-arrow')) return;
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('width', '0');
    svg.setAttribute('height', '0');
    svg.setAttribute('aria-hidden', 'true');
    svg.style.position = 'absolute';
    svg.innerHTML = '<defs><marker id="ukbatch-arrow" markerUnits="userSpaceOnUse" viewBox="0 0 10 10"' +
        ' refX="8" refY="5" markerWidth="9" markerHeight="9" orient="0">' +
        '<path d="M1,1 L9,5 L1,9 z"></path></marker></defs>';
    document.body.appendChild(svg);
}

// XSS: every interpolated value passes through escapeHtml — Blazor's output encoding does NOT apply to
// the DOM this module injects, so an unescaped step / service name would be an injection seam.
function escapeHtml(s) { const d = document.createElement('div'); d.textContent = s ?? ''; return d.innerHTML; }

// Compact node label: the last dotted segment of a namespaced job name
// ("Sample.Dashboard.Jobs.ArchiveJob" -> "ArchiveJob"). The FULL title stays in the tooltip.
function displayTitle(s) {
    const full = String(s ?? '');
    const i = full.lastIndexOf('.');
    return i >= 0 && i < full.length - 1 ? full.slice(i + 1) : full;
}

// CSS.escape guards an attribute selector built from a StepId; conservative manual fallback on the
// (rare) engines without CSS.escape so querySelector never throws.
function cssEscape(s) {
    if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function') return CSS.escape(String(s));
    return String(s).replace(/["'\\\]\[]/g, '\\$&');
}

export async function init(containerEl, dotnetRef, opts) {
    // opts: { zoomMin, zoomMax }
    const Drawflow = await loadDrawflow();   // classic-script UMD load — sets window.Drawflow
    await ensureDrawflowCss();                // AWAIT: nodes must be position:absolute BEFORE connections draw
    ensureArrowMarker();
    const editor = new Drawflow(containerEl);
    editor.reroute = false;
    editor.editor_mode = 'fixed';            // pan + zoom, NO node drag, NO connection edit
    editor.zoom_min = (opts && opts.zoomMin) ?? 0.4;
    editor.zoom_max = (opts && opts.zoomMax) ?? 2.0;
    editor.start();
    // Read-only monitoring view: disable WHEEL/PINCH zoom. Drawflow's zoom_enter fires on wheel when
    // ctrlKey is set — and on macOS a trackpad PINCH is exactly a ctrlKey wheel, so the canvas zoomed
    // in/out unexpectedly while the operator was scrolling/panning the page ("ekran zoom in-out yapıyor").
    // Zoom stays available via the explicit toolbar buttons (zoom_in/zoom_out call sites are unaffected).
    editor.zoom_enter = function () { /* no-op: wheel/pinch must not zoom; buttons only */ };

    const st = {
        editor,
        idMap: new Map(),       // String(dfId) -> stepId
        dfByStep: new Map(),    // stepId -> String(dfId)   (reverse map, O(1) lookups)
        onNodeClick: null,
    };
    _state.set(containerEl, st);

    // SELECTION — the primary JS→C# callback. 'fixed' mode does NOT raise `nodeSelected`:
    // delegate on the container, mirror dag-editor.js onEditClick. One discrete click → no per-pixel flood.
    const onNodeClick = (e) => {
        const card = e.target.closest('.drawflow-node');   // id = "node-<dfId>"
        if (!card) return;                                  // background click → Drawflow pans, no callback
        const stepId = st.idMap.get(card.id.slice(5));      // strip "node-"
        if (!stepId) return;
        // In-node Approve — checked FIRST, then return. The button lives inside the gate
        // card, so the closest('.drawflow-node') resolution above already gave us its StepId; we route
        // to the approve callback and DO NOT fall through to the generic node-select branch (the .NET
        // approve handler selects the node itself, so a double node-select would be redundant). Reject is
        // panel-only, so there is no symmetric reject branch here.
        if (e.target.closest('.dag-st-approve')) {
            dotnetRef.invokeMethodAsync('OnApproveClickedFromJs', stepId);
            return;
        }
        dotnetRef.invokeMethodAsync('OnNodeSelectedFromJs', stepId);
    };
    st.onNodeClick = onNodeClick;
    containerEl.addEventListener('click', onNodeClick);

    // DRAG-PAN. Drawflow's own 'fixed'-mode background pan is unreliable here (it left the precanvas
    // transform untouched), so we own panning: grab empty canvas → translate the precanvas. A mousedown
    // ON a node is left alone (that path is the selection click). We move canvas_x/canvas_y (Drawflow's
    // pan state) and write the same transform string Drawflow uses, so toolbar zoom/reset stay consistent.
    let panning = false, panStartX = 0, panStartY = 0, panBaseX = 0, panBaseY = 0, panMoved = false;
    const applyTransform = () => {
        if (editor.precanvas)
            editor.precanvas.style.transform =
                'translate(' + editor.canvas_x + 'px, ' + editor.canvas_y + 'px) scale(' + editor.zoom + ')';
    };
    const onPanDown = (e) => {
        if (e.button !== 0) return;                       // left button only
        if (e.target.closest('.drawflow-node')) return;   // node → selection, not pan
        panning = true; panMoved = false;
        panStartX = e.clientX; panStartY = e.clientY;
        panBaseX = editor.canvas_x || 0; panBaseY = editor.canvas_y || 0;
    };
    const onPanMove = (e) => {
        if (!panning) return;
        const dx = e.clientX - panStartX, dy = e.clientY - panStartY;
        if (!panMoved && Math.abs(dx) + Math.abs(dy) < 3) return;   // ignore micro-jitter so a click still selects
        panMoved = true;
        editor.canvas_x = panBaseX + dx;
        editor.canvas_y = panBaseY + dy;
        applyTransform();
    };
    const onPanUp = () => { panning = false; };
    st.onPanDown = onPanDown; st.onPanMove = onPanMove; st.onPanUp = onPanUp;
    containerEl.addEventListener('mousedown', onPanDown);
    document.addEventListener('mousemove', onPanMove);
    document.addEventListener('mouseup', onPanUp);
    // No node-move / connection-create / connection-remove / drop / drag-over handlers — read-only.

    return {
        buildGraph: (graph) => buildGraphImpl(st, graph),
        setStatuses: (m) => setStatusesImpl(st, m),
        setPending: (stepIds) => setPendingImpl(st, stepIds),   // reveal the in-node Approve
        selectNode: (stepId) => selectImpl(st, stepId),
        zoomIn: () => editor.zoom_in(),
        zoomOut: () => editor.zoom_out(),
        resetView: () => resetViewImpl(st),     // zoom_reset only restores zoom=1; also reset pan
    };
}

function resetViewImpl(st) {
    // zoom_reset() restores zoom=1 but leaves canvas_x/canvas_y (pan). Reset pan too, then refresh transform.
    st.editor.zoom_reset();
    st.editor.canvas_x = 0;
    st.editor.canvas_y = 0;
    if (st.editor.precanvas) {
        st.editor.precanvas.style.transform = 'translate(0px, 0px) scale(' + st.editor.zoom + ')';
    }
}

// Node card HTML. base class + data-status drive the CSS; the head carries the title + a status-icon
// slot (a ::before driven by [data-status]). EVERY interpolated value escaped (XSS).
function nodeHtml(spec) {
    const sid = escapeHtml(spec.stepId);
    const kind = escapeHtml(String(spec.kind).toLowerCase());
    const status = escapeHtml(spec.statusClass ?? '');
    const cloud = spec.targetService
        ? `<span class="dag-st-node__cloud" title="Runs on remote service"><span class="material-symbols-outlined">cloud</span>${escapeHtml(spec.targetService)}</span>`
        : '';
    const sub = spec.subtitle
        ? `<span class="dag-st-node__sub">${escapeHtml(spec.subtitle)}</span>`
        : '';
    const isApproval = String(spec.kind) === 'Approval';
    const approvalIco = isApproval
        ? `<span class="material-symbols-outlined dag-st-node__kindico">rule</span>`
        : '';
    // In-node Approve: rendered on Approval nodes ONLY, hidden by default. CSS reveals it
    // solely when the node carries data-pending="true" (the JS bridge sets that from PendingStepIds — see
    // setPendingImpl). A plain non-interactive Drawflow node has pointer-events:none on its ports but the
    // card itself is clickable; this button is caught by the delegated container click BEFORE the generic
    // node-select branch (see onNodeClick). Reject stays panel-only (a reason is mandatory).
    const approveBtn = isApproval
        ? `<button class="dag-st-approve" type="button" title="Approve this gate"><span class="material-symbols-outlined">check</span>Approve</button>`
        : '';
    return `<div class="dag-st-node dag-st-node--${kind}" data-step="${sid}" data-status="${status}">
              <div class="dag-st-node__head">
                ${approvalIco}
                <span class="dag-st-node__title" title="${escapeHtml(spec.title)}">${escapeHtml(displayTitle(spec.title))}</span>
                <span class="dag-st-node__notice material-symbols-outlined" aria-hidden="true"></span>
              </div>
              <div class="dag-st-node__meta">${sub}${cloud}</div>
              ${approveBtn}
            </div>`;
}

function buildGraphImpl(st, graph) {
    st.editor.clear();
    st.idMap.clear();
    st.dfByStep.clear();
    const nodes = (graph && graph.nodes) || [];
    for (const n of nodes) {
        // addNode(name, inputs, outputs, posx, posy, class, data, html, typenode). 1 in + 1 out: the
        // n8n flow look. Ports are non-interactive (CSS pointer-events:none) — no operator edge edit.
        const dfId = st.editor.addNode(
            n.kind, 1, 1, n.x, n.y, `dag-st-${String(n.kind).toLowerCase()}`,
            { stepId: n.stepId }, nodeHtml(n), false);
        st.idMap.set(String(dfId), n.stepId);
        st.dfByStep.set(n.stepId, String(dfId));    // reverse map
    }
    // CRITICAL: force a synchronous layout/reflow BEFORE drawing connections. Drawflow computes each
    // connection's SVG path from the node PORT positions at addConnection time. addNode only inserts the
    // node HTML — the browser has not laid it out yet, so its height is ~0 and the centred port sits near
    // y=0. Without this flush, every connection draws as a flat segment pinned to the canvas top ("arrows
    // detached at the top" bug). Reading offsetHeight flushes pending layout so ports report real y.
    void st.editor.container.offsetHeight;
    const edges = (graph && graph.edges) || [];
    for (const e of edges) {
        const a = st.dfByStep.get(e.fromStepId);
        const b = st.dfByStep.get(e.toStepId);
        if (a != null && b != null) st.editor.addConnection(a, b, 'output_1', 'input_1');
    }
    applyEdgeKinds(st, graph);   // tag each .connection with data-kind + data-edgeid
    // Belt-and-braces: after another reflow, recompute every connection from the nodes' FINAL port
    // positions (Drawflow's per-node reflow method, same as the editor uses). Covers any residual settle.
    void st.editor.container.offsetHeight;
    if (typeof st.editor.updateConnectionNodes === 'function') {
        for (const df of st.idMap.keys()) st.editor.updateConnectionNodes('node-' + df);
    }
    applyArrowMarkers(st);
}

// Derive data-edgeid + data-kind from Drawflow's OWN node_out_node-X / node_in_node-Y
// connection classes (authoritative source/target encoding, verified in addConnection), NOT append order.
function applyEdgeKinds(st, graph) {
    const edges = (graph && graph.edges) || [];
    // Precompute { "node_out_node-A|node_in_node-B" -> { kind, edgeid } } from the graph.
    const meta = new Map();
    for (const e of edges) {
        const a = st.dfByStep.get(e.fromStepId);
        const b = st.dfByStep.get(e.toStepId);
        if (a == null || b == null) continue;
        meta.set(`node_out_node-${a}|node_in_node-${b}`, {
            kind: e.kind,
            status: e.statusClass ?? '',
            edgeid: `${e.fromStepId}->${e.toStepId}`,
        });
    }
    for (const conn of st.editor.container.querySelectorAll('.connection')) {
        const cl = conn.classList;
        const out = [...cl].find(c => c.startsWith('node_out_node-'));   // matches click()'s slice(14) source
        const inn = [...cl].find(c => c.startsWith('node_in_node-'));    // matches click()'s slice(13) target
        if (!out || !inn) continue;
        const m = meta.get(`${out}|${inn}`);
        if (m) {
            conn.dataset.kind = m.kind;
            conn.dataset.edgeid = m.edgeid;
            if (m.status) conn.dataset.status = m.status;   // initial edge tint (subsequent via setStatuses)
        }
    }
}

// Status update WITHOUT node rebuild: single data-status attribute writes on existing node DOM +
// connection elements. Never clear()+re-add. O(n) via the dfByStep reverse map + the authoritative edgeid.
function setStatusesImpl(st, m) {
    const nodes = (m && m.nodes) || {};
    for (const stepId of Object.keys(nodes)) {
        const df = st.dfByStep.get(stepId);
        if (df == null) continue;                                       // O(1) reverse map
        const el = st.editor.container.querySelector(`#node-${df} .dag-st-node`);
        if (el) el.dataset.status = nodes[stepId];                      // single-attr write — never clobbers kind/selected
    }
    const edges = (m && m.edges) || {};
    for (const edgeId of Object.keys(edges)) {
        const conn = st.editor.container.querySelector(`.connection[data-edgeid="${cssEscape(edgeId)}"]`);
        if (conn) conn.dataset.status = edges[edgeId];                  // targeted by authoritative edgeid
    }
}

// Pending-gate flag. FULL-SET reconcile: every node whose StepId is in `stepIds` gets
// data-pending="true" (CSS reveals its in-node Approve); EVERY other node has the flag cleared — so a
// gate that was just approved (drops out of the pending set) loses its button on the next push. Driven by
// an explicit set, NOT by data-status, because AwaitingApproval is visually mapped to "running" (the amber
// pulse) — too indirect to mean "decidable now" (it would also light up on Running jobs).
function setPendingImpl(st, stepIds) {
    const pending = new Set((stepIds || []).map(String));
    for (const [df, stepId] of st.idMap) {
        const el = st.editor.container.querySelector(`#node-${df} .dag-st-node`);
        if (!el) continue;
        if (pending.has(String(stepId))) el.dataset.pending = 'true';
        else delete el.dataset.pending;
    }
}

function selectImpl(st, stepId) {
    const df = stepId != null ? st.dfByStep.get(stepId) : null;
    const selectedId = df != null ? `node-${df}` : null;
    st.editor.container.querySelectorAll('.drawflow-node .dag-st-node--selected').forEach(n => {
        if (!selectedId || n.closest('.drawflow-node')?.id !== selectedId) n.classList.remove('dag-st-node--selected');
    });
    if (selectedId) {
        st.editor.container.querySelector(`#${selectedId} .dag-st-node`)?.classList.add('dag-st-node--selected');
    }
}

// Belt-and-braces arrowhead: set marker-end as an SVG ATTRIBUTE on every connection's .main-path (CSS
// marker-end is honoured inconsistently across engines). Idempotent. Read-only canvas never re-renders
// connections after buildGraph, so this runs once.
function applyArrowMarkers(st) {
    const paths = st.editor.container.querySelectorAll('.connection .main-path');
    for (const p of paths) p.setAttribute('marker-end', 'url(#ukbatch-arrow)');
}

export function dispose(containerEl) {
    const st = _state.get(containerEl);
    if (st) {
        if (st.onNodeClick) containerEl.removeEventListener('click', st.onNodeClick);  // symmetric teardown (mirror dag-editor.js)
        if (st.onPanDown) containerEl.removeEventListener('mousedown', st.onPanDown);
        if (st.onPanMove) document.removeEventListener('mousemove', st.onPanMove);
        if (st.onPanUp) document.removeEventListener('mouseup', st.onPanUp);
        try { st.editor.clear(); } catch { /* already torn down */ }
    }
    _state.delete(containerEl);
}
