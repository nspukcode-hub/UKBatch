// UKBatch.Dashboard Drawflow bridge — ES module.
//
// Mirrors dag-canvas.js: init() returns a controller, per-instance WeakMap state, dispose() teardown.
//
// COMMIT-ONLY: Drawflow owns the live drag (DOM transform, pointer math). C#
// receives ONLY five discrete committed events via dotnetRef — OnNodeMoved (pointer-up, ~120ms
// debounced), OnNodeSelected, OnNodeDropped, OnNodeRemoved, OnNodeEditRequested. ALL five are
// discrete user actions (settle / click / drop / delete / edit-button click). There is NO
// per-frame/per-pixel invokeMethodAsync; a fast drag-burst produces ONE SignalR frame at settle, not
// per pixel. OnNodeEditRequested fires ONLY from a hover-Edit-button click (a single discrete action),
// never from a high-frequency pointer handler — so the per-pixel invariant still holds.
//
// SOURCE-OF-TRUTH: C# owns the model + execution order; this canvas owns ONLY node (x,y)
// hints. Drawflow's connection graph is NEVER persisted — execution order is the C# Steps list. The
// nodeRemoved → C# path reports ONLY operator-initiated deletes; C#-initiated removes are suppressed
// (st.suppressRemovedEvent) so the round-trip does not double-remove.

// Vendored Drawflow is UMD-only (no ESM dist exists for v0.0.60). Loading a UMD bundle via a STATIC
// ESM `import` is unreliable across browsers: the bundle is not an ES module, and under ESM strict
// mode (top-level `this` is undefined) whether it attaches to a global is brittle — empirically it
// left globalThis.Drawflow unset and init threw, degrading the editor to its fallback banner (smoke
// G.7). The robust, browser-agnostic way to consume a UMD from a module is to inject it as a CLASSIC
// <script> (top-level `this`/`self` === window there, so the UMD's else-branch sets window.Drawflow),
// await onload, then read the global. Still LAZY: loadDrawflow() runs only when the Editor page
// imports THIS module + init() is called — never on non-editor pages, and NO <script> in App.razor.
const _state = new WeakMap();

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

// One-time SVG <marker> def for the n8n-style connection arrowheads. `marker-end: url(#ukbatch-arrow)`
// on .main-path (dashboard.css) references it; `fill: context-stroke` makes the arrow inherit the line's
// stroke so it tracks the theme colour AND the hover colour. Idempotent (id guard) — shared by all canvases.
function ensureArrowMarker() {
    if (document.getElementById('ukbatch-arrow')) return;
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('width', '0');
    svg.setAttribute('height', '0');
    svg.setAttribute('aria-hidden', 'true');
    svg.style.position = 'absolute';
    // markerUnits=userSpaceOnUse → ABSOLUTE 9px (the default `strokeWidth` units scale by the 2px
    // stroke → a huge arrow). orient=0 → always points right (→): our input ports are always approached
    // from the left, so a fixed horizontal arrow reads like n8n (no tilt from the bezier's end-tangent).
    // Fill comes from CSS (#ukbatch-arrow path) — NOT a context-stroke attr (black on older WebKit).
    svg.innerHTML = '<defs><marker id="ukbatch-arrow" markerUnits="userSpaceOnUse" viewBox="0 0 10 10"' +
        ' refX="8" refY="5" markerWidth="9" markerHeight="9" orient="0">' +
        '<path d="M1,1 L9,5 L1,9 z"></path></marker></defs>';
    document.body.appendChild(svg);
}

export async function init(containerEl, dotnetRef, opts) {
    // opts: { readOnly: bool, zoomMin, zoomMax }
    const Drawflow = await loadDrawflow();  // classic-script UMD load (see note) — sets window.Drawflow
    ensureArrowMarker();                    // n8n-style connection arrowheads (one-time SVG marker def)
    const editor = new Drawflow(containerEl);
    editor.reroute = false;                 // edges are NOT execution semantics — order is the C# Steps list
    editor.editor_mode = opts.readOnly ? 'view' : 'edit';
    editor.zoom_min = opts.zoomMin ?? 0.4;
    editor.zoom_max = opts.zoomMax ?? 2.0;
    editor.start();

    // ── B-2: state object declared ABOVE all editor.on(...) registrations ──
    // The nodeRemoved handler dereferences `st.idMap`; declaring `st` after the handler registration
    // would TDZ-ReferenceError on the first canvas Delete. idMap keys are STRING everywhere
    // (Drawflow node ids arrive as numbers from some callbacks, strings from others — normalize once).
    const moveTimers = new Map();           // stepId -> timeout handle
    const st = {
        editor,
        idMap: new Map(),                   // String(drawflowId) -> stepId
        moveTimers,
        suppressRemovedEvent: false,        // set around a C#-initiated removeNodeId (see removeNodeImpl)
        deleteProtected: new Set(),         // stepIds the canvas must refuse to delete (see the wrapper below)
        allowProtectedRemove: false,        // lets removeNodeImpl bypass that refusal for a C#-driven remove
        onDragOver: null,                   // assigned below; cached for symmetric removal in dispose()
        onDrop: null,
        onEditPointerDown: null,            // capture-phase guard: stops Drawflow's drag on the Edit btn
        onEditClick: null,                  // delegated click: opens the modal for the node
        // ── connection bookkeeping (visual-only edges; see syncConnectionsImpl) ──
        syncing: false,                     // re-entrancy guard: our own addConnection/removeConnection
                                            // fire connectionCreated/Removed — don't recurse into sync
        lastEdges: [],                      // last C#-pushed TYPED edge set ({fromStepId,toStepId,kind}[]);
                                            // the guardResync / refreshNode-fallback revert target
    };
    _state.set(containerEl, st);

    // ── delete guard for cards the canvas must not remove ──────────────────────────────────────────
    // Some cards PROJECT a field of another step (a decision's branch) rather than being a step of their
    // own: there is no entry in the C# Steps list a canvas delete could remove, so allowing one would
    // leave the model and the canvas disagreeing. Such a card is removed by editing its parent.
    //
    // Drawflow routes EVERY delete surface — the Delete key (its `key` handler), the right-click "x"
    // (its `drawflow-delete` case), and our own toolbar button — through removeNodeId, so wrapping that
    // ONE method covers all of them. A key-event guard would not: Drawflow binds keydown on this same
    // container in start(), and for an event whose target IS the container there is no capture/bubble
    // ordering to win — registration order decides, and Drawflow registered first.
    //
    // clear() empties the precanvas directly (it never calls removeNodeId), so importGraph and dispose
    // are unaffected by this wrapper.
    const nativeRemoveNodeId = editor.removeNodeId.bind(editor);
    editor.removeNodeId = (domId) => {
        const stepId = st.idMap.get(String(domId).slice(5));   // "node-<df>" -> "<df>"
        if (stepId != null && st.deleteProtected.has(stepId) && !st.allowProtectedRemove) return;
        nativeRemoveNodeId(domId);
    };

    // ── debounced move commit (pointer-up granularity, NOT per-frame) ──
    function commitMove(drawflowNodeId) {
        const node = editor.getNodeFromId(drawflowNodeId);
        const stepId = node?.data?.stepId;
        if (!stepId) return;
        clearTimeout(moveTimers.get(stepId));
        moveTimers.set(stepId, setTimeout(() => {
            const n = editor.getNodeFromId(drawflowNodeId);
            if (!n) return;
            // Fire-and-forget. ONE call per settle, ~120ms after the last position event.
            dotnetRef.invokeMethodAsync('OnNodeMoved', stepId, n.pos_x, n.pos_y);
        }, 120));
    }

    editor.on('nodeMoved', (drawflowNodeId) => commitMove(drawflowNodeId));
    editor.on('nodeSelected', (drawflowNodeId) => {
        const node = editor.getNodeFromId(drawflowNodeId);
        if (node?.data?.stepId) dotnetRef.invokeMethodAsync('OnNodeSelected', node.data.stepId);
    });
    editor.on('nodeRemoved', (drawflowNodeId) => {
        // B-2: C# is the source of truth. A C#-initiated removeNode (removeNodeImpl) sets
        // suppressRemovedEvent so we do NOT echo it back as an operator delete (re-entrancy /
        // double-remove guard). Only operator-initiated Deletes reach C#.
        const key = String(drawflowNodeId);
        const stepId = st.idMap.get(key);
        st.idMap.delete(key);
        if (stepId) st.deleteProtected.delete(stepId);   // before the suppress check: the node is gone either way
        if (st.suppressRemovedEvent) return;
        if (stepId) dotnetRef.invokeMethodAsync('OnNodeRemoved', stepId);
    });

    // ── connection guard (edges are VISUAL ONLY, never operator-editable) ──────────────────────────
    // The n8n "flow" lines visualize the C# execution order (the Steps list). They are NOT the source
    // of truth — the operator cannot draw or delete them (the port dots are CSS pointer-events:none, so
    // there is no normal way to). This is defense-in-depth: if a connection is somehow created or
    // removed outside our programmatic syncConnections (e.g. a future Drawflow API surface), revert by
    // re-running the authoritative sync. These handlers NEVER call back to .NET — only a local DOM
    // re-sync (no per-pixel invoke; G.3 invariant holds).
    //
    // Two guards make this safe: (1) `st.syncing` suppresses the events OUR OWN add/removeConnection
    // raise (synchronous dispatch). (2) `removeNodeId` internally fires connectionRemoved BEFORE
    // nodeRemoved (so mid-delete the dying node is still in idMap) — deferring to a microtask lets the
    // node teardown + the C# structural-change SyncConnectionsAsync settle first, and re-syncing from
    // `lastEdges` FILTERED to still-resolvable nodes (syncConnectionsImpl already drops null dfIds)
    // means a just-removed node is never re-wired.
    const guardResync = () => {
        if (st.syncing) return;
        queueMicrotask(() => { if (!st.syncing) syncConnectionsImpl(st, st.lastEdges); });
    };
    editor.on('connectionCreated', guardResync);
    editor.on('connectionRemoved', guardResync);

    // ── HTML5 drop (palette → canvas) handled JS-SIDE (NOT a Blazor @ondrop) ──
    // dragover MUST preventDefault to allow the drop, but it does so in plain JS with NO .NET
    // round-trip. Only the final drop reports intent (step 1 of the two-step add).
    const onDragOver = (e) => { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy'; };
    const onDrop = (e) => {
        e.preventDefault();
        const kind = e.dataTransfer?.getData('application/x-ukbatch-step');  // 'Job'|'ParallelGroup'|'ApprovalGate'
        if (!kind) return;
        const rect = containerEl.getBoundingClientRect();
        // translate client coords → Drawflow canvas coords (account for pan/zoom)
        const z = editor.zoom || 1;
        const x = (e.clientX - rect.left - editor.canvas_x) / z;
        const y = (e.clientY - rect.top - editor.canvas_y) / z;
        // STEP 1 of the two-step add: report intent ONLY. C# mints the StepId + draft, then calls addNode.
        dotnetRef.invokeMethodAsync('OnNodeDropped', kind, x, y);
    };
    st.onDragOver = onDragOver;
    st.onDrop = onDrop;
    containerEl.addEventListener('dragover', onDragOver);
    containerEl.addEventListener('drop', onDrop);

    // ── hover Edit button → open the modal, WITHOUT moving the node (n8n decouple move/edit) ──
    // Move = node-body drag (Drawflow). Edit = a small button overlaid on the node on hover. The two
    // are decoupled here. Two delegated listeners on containerEl (NOT per-node — survives re-renders):
    //
    // (1) CAPTURE-phase pointerdown+mousedown guard. CRITICAL: Drawflow starts the node drag in
    //     `this.click`, bound to `container.addEventListener("mousedown", ...)` (BUBBLE phase) +
    //     `container.onpointerdown` (Safari/WebKit fires pointer events too). To stop a click on the
    //     Edit button from beginning a drag, we intercept on the SAME container in the CAPTURE phase
    //     (runs BEFORE Drawflow's bubble-phase handler) and stopPropagation when the target is the
    //     Edit button. We stop BOTH pointerdown and mousedown: stopping only mousedown would let
    //     `pointerdown_handler` still arm a drag on WebKit. preventDefault is NOT called (we don't want
    //     to suppress the subsequent click — only keep the event from reaching Drawflow's node handler).
    // (2) BUBBLE-phase click → the discrete edit intent. Reads the StepId off the button's data-edit
    //     (fallback: the closest .dag-ed-node's data-step) and reports it to C# (one invoke per click —
    //     NOT per-pixel, so the no-high-frequency-round-trip invariant holds; this is the 5th committed callback).
    const onEditPointerDown = (e) => {
        if (e.target.closest('.dag-ed-node__act')) e.stopPropagation();  // any toolbar button: don't start a drag
    };
    const onEditClick = (e) => {
        const act = e.target.closest('.dag-ed-node__act');
        if (!act) return;
        e.stopPropagation();
        e.preventDefault();
        if (act.classList.contains('dag-ed-node__del')) {
            // Operator-initiated delete: removeNodeId fires Drawflow's nodeRemoved (NOT suppressed —
            // suppress is only for C#-initiated removes) → OnNodeRemoved → C# drops the step + resyncs.
            // Same path as the canvas Delete key; no new .NET callback.
            const stepId = act.dataset.del ?? act.closest('.dag-ed-node')?.dataset.step;
            const df = stepId != null ? findDfId(st, stepId) : null;
            if (df != null) editor.removeNodeId('node-' + df);
            return;
        }
        const stepId = act.dataset.edit ?? act.closest('.dag-ed-node')?.dataset.step;
        if (stepId) dotnetRef.invokeMethodAsync('OnNodeEditRequested', stepId);  // a single discrete click — no high-frequency round-trip
    };
    st.onEditPointerDown = onEditPointerDown;
    st.onEditClick = onEditClick;
    containerEl.addEventListener('pointerdown', onEditPointerDown, true);  // capture: pre-empt the drag
    containerEl.addEventListener('mousedown', onEditPointerDown, true);    // capture: pre-empt the drag
    containerEl.addEventListener('click', onEditClick);

    // Controller surface (C# → JS). No invokeMethodAsync here — these are pure DOM mutations.
    return {
        // STEP 2 of the two-step add: C# minted the draft+StepId, now JS places the node.
        addNode: (spec) => addNodeImpl(st, spec),
        removeNode: (stepId) => removeNodeImpl(st, stepId),
        updateNodeLabel: (stepId, title, orderBadge) => updateLabelImpl(st, stepId, title, orderBadge),
        // Re-render ONE node's inner card from a fresh spec (e.g. a ParallelGroup whose children changed
        // in the modal) + recompute its connection anchors (the card height changed → its centred ports
        // moved). Pure C#→JS DOM op — NO invokeMethodAsync (the per-pixel invariant holds).
        refreshNode: (spec) => refreshNodeImpl(st, spec),
        importGraph: (graph) => importImpl(st, graph),
        // Redraw the visual flow lines from the C# TYPED edge set ({fromStepId,toStepId,kind}[]): main
        // flow Sequential + the red-dashed OnFailure compensation branch. Pure C#→JS DOM op — NO
        // invokeMethodAsync. Edges are presentation, not semantics (the C# lists are the source of truth).
        syncConnections: (edges) => syncConnectionsImpl(st, edges),
        setReadOnly: (ro) => { st.editor.editor_mode = ro ? 'view' : 'edit'; },
        selectNode: (stepId) => selectImpl(st, stepId),   // rail → canvas selection link
    };
}

// EVERY interpolated string passes through escapeHtml — title, subtitle, AND targetService.
// Blazor's output encoding does NOT apply to DOM this module injects, so an unescaped step name or
// service name would be an XSS seam.
function nodeHtml(spec) {
    // spec: { stepId, kind, title, subtitle, orderBadge, targetService, children, branches,
    //         isOnFailure, isDeleteProtected, branchAccent }
    const cloud = spec.targetService
        ? `<span class="dag-ed-node__cloud"><span class="material-symbols-outlined">cloud</span>${escapeHtml(spec.targetService)}</span>`
        : '';
    // ParallelGroup nodes render their child jobs INSIDE the card as parallel branch chips (visualize
    // the contents at a glance, no modal needed). DISPLAY-ONLY — the editable model is the C# draft's
    // Children. Each chip shows the (shortened) job name + a fork icon; the FULL name is in the tooltip.
    // The container grows the card taller as children are added (refreshNode keeps the flow lines
    // attached). Empty / non-group → '' (nodeHtml is unchanged for Job/ApprovalGate).
    const branches = branchesHtml(spec);
    // Hover-only action toolbar, FLOATING ABOVE the node (n8n pattern). Hidden by CSS
    // (opacity:0 / pointer-events:none) until .dag-ed-node:hover, then a small bar of icon buttons.
    // Edit → opens the modal (OnNodeEditRequested). Delete → removes the node via the existing
    // remove flow (the delegated click handler calls editor.removeNodeId → Drawflow nodeRemoved →
    // OnNodeRemoved → C#; no new callback). tabindex=-1: the rail chip is the keyboard edit path.
    // data-edit / data-del carry the StepId for the delegated listener; escaped (XSS discipline).
    const sid = escapeHtml(spec.stepId);
    // A delete-protected card (a decision's branch) gets NO Delete button: it has no model entry of its
    // own to remove — it is dropped in its parent's dialog. Offering the button and then refusing the
    // delete would read as a broken button.
    const delBtn = spec.isDeleteProtected ? '' :
        `<button type="button" class="dag-ed-node__act dag-ed-node__del" data-del="${sid}" title="Delete" tabindex="-1"><span class="material-symbols-outlined">delete</span></button>`;
    const toolbar = `<div class="dag-ed-node__toolbar">` +
        `<button type="button" class="dag-ed-node__act dag-ed-node__edit" data-edit="${sid}" title="Edit" tabindex="-1"><span class="material-symbols-outlined">edit</span></button>` +
        delBtn +
        `</div>`;
    // Compensation (onFailure) nodes keep their OUTER Drawflow class as `dag-ed-job` (the modal keys off
    // Kind=Job for the Job-only editor) but the INNER card carries the `dag-ed-node--failure` modifier
    // (red/dashed accent — mirrors the read-only compensation styling).
    const failureMod = spec.isOnFailure ? ' dag-ed-node--failure' : '';
    // A decision branch card takes its branch's colour, matching the chip inside the diamond and the edge
    // between them. data-branch drives it (CSS maps the key to a palette slot); absent on every other card.
    const accent = spec.branchAccent ? ` data-branch="${escapeHtml(String(spec.branchAccent))}"` : '';
    const hint = spec.isDeleteProtected ? ` title="Edit this branch in the decision's dialog"` : '';
    return `<div class="dag-ed-node dag-ed-node--${escapeHtml(String(spec.kind).toLowerCase())}${failureMod}" data-step="${sid}"${accent}${hint}>
              ${toolbar}
              <span class="dag-ed-node__badge">${escapeHtml(spec.orderBadge ?? '')}</span>
              <span class="dag-ed-node__title" title="${escapeHtml(spec.title)}">${escapeHtml(displayTitle(spec.title))}</span>
              <span class="dag-ed-node__sub">${escapeHtml(spec.subtitle ?? '')}</span>${branches}${cloud}
            </div>`;
}

// Branches block for a Decision (routing conditions) OR a ParallelGroup (child jobs) node. Every label
// passes through escapeHtml (XSS — same discipline as nodeHtml; Blazor's encoder does NOT cover injected
// DOM). Returns '' for a node with neither (Job/ApprovalGate cards unchanged).
function branchesHtml(spec) {
    if (spec.branches && spec.branches.length) return decisionChipsHtml(spec.branches);
    if (spec.children && spec.children.length) return childChipsHtml(spec.children);
    return '';
}

// Decision chips: one per branch, each showing its routing condition IN FULL and carrying the colour of
// the edge to its branch card — colour is what pairs chip to card, since the edges carry no text.
// NB: the label is NOT run through displayTitle. That shortener strips everything before the last dot,
// which is right for a namespaced job name but would mangle a dotted parameter key
// ("order.amount > 1000" -> "amount > 1000"). CSS ellipsis handles overflow; the tooltip has the full text.
function decisionChipsHtml(branches) {
    const chips = branches.map(b => {
        const label = String(b?.label ?? '');
        const accent = b?.accent ? ` data-branch="${escapeHtml(String(b.accent))}"` : '';
        return `<span class="dag-ed-node__branch"${accent} title="${escapeHtml(label)}">` +
            `<span class="material-symbols-outlined dag-ed-node__branch-ico">call_split</span>` +
            `<span class="dag-ed-node__branch-name">${escapeHtml(label)}</span></span>`;
    }).join('');
    return `<div class="dag-ed-node__branches dag-ed-node__branches--decision">${chips}</div>`;
}

// ParallelGroup child chips: one per child job, each shortened via displayTitle with the FULL name in the
// title attr (matches the node title's display rule).
function childChipsHtml(children) {
    const chips = children.map(c => {
        const full = String(c ?? '');
        return `<span class="dag-ed-node__branch" title="${escapeHtml(full)}">` +
            `<span class="material-symbols-outlined dag-ed-node__branch-ico">fork_right</span>` +
            `<span class="dag-ed-node__branch-name">${escapeHtml(displayTitle(full))}</span></span>`;
    }).join('');
    return `<div class="dag-ed-node__branches">${chips}</div>`;
}

// Encodes < > & via the DOM, then the two quote characters so the result is safe inside a
// double- or single-quoted attribute (every interpolation site here is a quoted attribute or
// element text). Without the quote replaces a value like a" onmouseover="..." would break out
// of title="..." / data-step="..." and inject a live event handler.
function escapeHtml(s) {
    const d = document.createElement('div');
    d.textContent = s ?? '';
    return d.innerHTML.replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

// Compact node label: the last dotted segment of a namespaced job name
// ("Sample.Dashboard.Jobs.ArchiveJob" -> "ArchiveJob"). Approval titles / "N branches" have no dots
// and pass through unchanged. The FULL title stays in the tooltip (title attr) + is the source value;
// this is display-only (keeps the card readable + n8n-like; does not change order/structure).
function displayTitle(s) {
    const full = String(s ?? '');
    const i = full.lastIndexOf('.');
    return i >= 0 && i < full.length - 1 ? full.slice(i + 1) : full;
}

function addNodeImpl(st, spec) {
    // Drawflow addNode(name, inputs, outputs, posx, posy, class, data, html, typenode).
    // 1 input + 1 output: the n8n "flow" look. The ports are NON-interactive (CSS pointer-events:none
    // on .input/.output) — the operator cannot drag a connection from them; only programmatic
    // syncConnectionsImpl draws edges (which doesn't need pointer events). The C# Steps list is the
    // execution-order source of truth; these edges only visualize it.
    // The `class` argument lands on the Drawflow WRAPPER, which is where the right-click "x" is
    // appended — so dag-ed-nodelete is what lets CSS hide that affordance on a protected card.
    const cls = `dag-ed-${String(spec.kind).toLowerCase()}${spec.isDeleteProtected ? ' dag-ed-nodelete' : ''}`;
    const dfId = st.editor.addNode(
        spec.kind, 1, 1, spec.x, spec.y, cls,
        { stepId: spec.stepId }, nodeHtml(spec), false);
    st.idMap.set(String(dfId), spec.stepId);
    if (spec.isDeleteProtected) st.deleteProtected.add(spec.stepId);
    return dfId;
}

function findDfId(st, stepId) {
    // O(n) linear scan. Acceptable at the realistic <50-step ceiling. NewStepId is the
    // shared round-trip contract and is NOT widened to carry a reverse index.
    for (const [df, sid] of st.idMap) if (sid === stepId) return df;
    return null;
}

function removeNodeImpl(st, stepId) {
    const df = findDfId(st, stepId);
    if (df == null) return;
    // B-2: suppress the echo so the resulting nodeRemoved does NOT re-report this C#-initiated
    // delete back to C#. The handler still cleans idMap; we also clean it here for the early path.
    // allowProtectedRemove bypasses the delete guard: the guard exists to stop an OPERATOR delete of a
    // card that has no model entry to remove — a C#-driven remove means the model already changed.
    st.suppressRemovedEvent = true;
    st.allowProtectedRemove = true;
    try { st.editor.removeNodeId(`node-${df}`); }
    finally { st.suppressRemovedEvent = false; st.allowProtectedRemove = false; }
    st.idMap.delete(String(df));
    st.deleteProtected.delete(stepId);
}

function updateLabelImpl(st, stepId, title, orderBadge) {
    const df = findDfId(st, stepId);
    if (df == null) return;
    const el = st.editor.container.querySelector(`#node-${df} [data-step="${cssEscape(stepId)}"]`);
    if (!el) return;
    const t = el.querySelector('.dag-ed-node__title'); if (t) { t.textContent = displayTitle(title); t.title = title ?? ''; }
    const b = el.querySelector('.dag-ed-node__badge'); if (b) b.textContent = orderBadge ?? '';
}

// Replace ONE node's inner card with a fresh nodeHtml(spec), then recompute its connection anchors.
// Used when a node's BODY changes shape (a ParallelGroup gaining/losing/renaming a branch grows or
// shrinks the card) — updateNodeLabel only patches the title/badge text and would leave a stale branch
// list + detached flow lines. CRITICAL: the card height changed, so the vertically-centred ports
// (CSS top: calc(50% - 5px)) moved; updateConnectionNodes('node-<df>') is Drawflow's method to recompute
// that node's connection geometry from the new port offsets — without it the n8n flow lines stay pinned
// to the OLD anchor and visibly detach from the resized node. Pure DOM (no .NET round-trip).
function refreshNodeImpl(st, spec) {
    const df = findDfId(st, spec.stepId);
    if (df == null) return;
    const content = st.editor.container.querySelector(`#node-${df} .drawflow_content_node`);
    if (!content) return;
    content.innerHTML = nodeHtml(spec);
    // Drawflow keys connection anchors off the FULL DOM id ("node-<df>", per its own internal calls).
    if (typeof st.editor.updateConnectionNodes === 'function') {
        st.editor.updateConnectionNodes('node-' + df);
    } else {
        // Fallback (vendored API surface changed): re-run the authoritative typed-edge sync, which
        // redraws every line from the C#-pushed edge set — the resized node's lines get recomputed too.
        syncConnectionsImpl(st, st.lastEdges);
    }
    applyArrowMarkers(st);   // re-assert the marker-end attr on any line Drawflow re-rendered
}

function selectImpl(st, stepId) {
    const df = findDfId(st, stepId);
    const selectedId = df != null ? `node-${df}` : null;
    st.editor.container.querySelectorAll('.drawflow-node.selected').forEach(n => {
        if (n.id !== selectedId) n.classList.remove('selected');
    });
    if (selectedId) st.editor.container.querySelector(`#${selectedId}`)?.classList.add('selected');
}

function importImpl(st, graph) {
    // graph: { nodes: [{stepId, kind, title, subtitle, orderBadge, targetService, isOnFailure, x, y}],
    //          edges: [{fromStepId, toStepId, kind}] }
    // C#-driven, so suppress the clear()'s nodeRemoved echoes.
    st.suppressRemovedEvent = true;
    try { st.editor.clear(); }
    finally { st.suppressRemovedEvent = false; }
    st.idMap.clear();
    st.deleteProtected.clear();         // re-registered per node by addNodeImpl below
    const nodes = graph?.nodes ?? [];
    for (const n of nodes) addNodeImpl(st, n);
    // Force a synchronous reflow BEFORE drawing connections. The onFailure node sits
    // on a LOWER lane (~260px below the spine); addNode only inserts the HTML, so without this flush the
    // browser has not laid the nodes out (height ~0) and the centred port sits near y=0 — the first
    // dashed edge then anchors at the canvas TOP ("arrows detached" bug).
    // Reading offsetHeight flushes pending layout so ports report their real (lower) y. Mirrors
    // dag-status.js buildGraphImpl exactly (flush between add-nodes and add-connections).
    void st.editor.container.offsetHeight;
    // Draw the flow lines from the C# TYPED edge set (main flow + the red-dashed OnFailure branch). The
    // initial set is wired here so the canvas opens "connected" without a separate C# round-trip.
    syncConnectionsImpl(st, graph?.edges ?? []);
    // Belt-and-braces: after another reflow, recompute every connection from the nodes' FINAL port
    // positions (covers any residual settle of the lower-lane node). Same as dag-status.js.
    void st.editor.container.offsetHeight;
    if (typeof st.editor.updateConnectionNodes === 'function') {
        for (const df of st.idMap.keys()) st.editor.updateConnectionNodes('node-' + df);
    }
}

// Redraw ALL visual flow lines from the supplied TYPED edge set ({fromStepId,toStepId,kind}[]): main
// flow Sequential + the red-dashed OnFailure compensation branch. Connections are derived from the
// MODEL edges, never from node geometry — a node dropped anywhere still connects per the C# topology,
// and a REORDER (which keeps the same dfIds but changes pairing) re-chains correctly. `st.syncing`
// suppresses the connectionCreated/Removed re-entrancy our own mutations would otherwise trigger.
function syncConnectionsImpl(st, edges) {
    const list = Array.isArray(edges) ? edges : [];
    st.lastEdges = list;                        // remember the authoritative edge set (revert target)
    st.syncing = true;
    try {
        // Clear EVERY connection first (not just the new pairs) so a reorder can't leave a stale edge
        // from the previous pairing. removeConnectionNodeId(dfId) drops all in/out edges of a node but
        // keeps the node DOM intact (editor.clear() would wipe nodes too). Iterate the FULL idMap.
        for (const df of st.idMap.keys()) st.editor.removeConnectionNodeId(`node-${df}`);
        // Wire each typed edge output_1 → input_1 (drop any edge whose endpoints aren't both resolvable —
        // e.g. a just-removed node). findDfId is the stepId → dfId reverse scan.
        for (const e of list) {
            const a = findDfId(st, e.fromStepId);
            const b = findDfId(st, e.toStepId);
            if (a != null && b != null) st.editor.addConnection(a, b, 'output_1', 'input_1');
        }
        applyEdgeKinds(st, list);   // tag each .connection with data-kind (authoritative class decode)
        applyArrowMarkers(st);
    } finally {
        st.syncing = false;
    }
}

// Tag each .connection with data-kind (+ data-branch on a decision's fan-out) from Drawflow's OWN
// node_out_node-<src> / node_in_node-<tgt> connection classes (the AUTHORITATIVE source/target encoding
// addConnection writes), NOT append order. Ported from dag-status.js applyEdgeKinds (the editor refuses to
// import the read-only module). The CSS rule .dag-ed-canvas .connection[data-kind="OnFailure"] .main-path
// renders the OnFailure branch red-dashed; data-branch resolves a decision edge to its branch colour.
function applyEdgeKinds(st, edges) {
    // Precompute { "node_out_node-A|node_in_node-B" -> edge } from the resolved typed edges.
    const meta = new Map();
    for (const e of edges) {
        const a = findDfId(st, e.fromStepId);
        const b = findDfId(st, e.toStepId);
        if (a == null || b == null) continue;
        meta.set(`node_out_node-${a}|node_in_node-${b}`, e);
    }
    for (const conn of st.editor.container.querySelectorAll('.connection')) {
        const cl = conn.classList;
        const out = [...cl].find(c => c.startsWith('node_out_node-'));
        const inn = [...cl].find(c => c.startsWith('node_in_node-'));
        if (!out || !inn) continue;
        const e = meta.get(`${out}|${inn}`);
        if (!e) continue;
        if (e.kind) conn.dataset.kind = e.kind;
        // The branch colour is what pairs this edge with its chip inside the diamond and the card it lands
        // on — the editor prints no text on edges, so colour carries the whole pairing. Delete rather than
        // leave a stale key: a re-sync reuses these .connection elements, and an edge that stopped being a
        // branch edge (its decision lost its branches) must not keep the old slot's colour.
        if (e.branchAccent) conn.dataset.branch = e.branchAccent; else delete conn.dataset.branch;
    }
}

// Belt-and-braces arrowhead: set marker-end as an SVG ATTRIBUTE on every connection's .main-path (in
// addition to the dashboard.css `marker-end` rule). CSS `marker-end` is honoured inconsistently across
// engines/Drawflow re-renders (a stroke restyle on hover can drop the CSS-resolved marker on some
// WebKit builds), whereas the presentation ATTRIBUTE is read directly off the element. The arrow def
// (#ukbatch-arrow) is small (9px userSpaceOnUse) and themed to the line colour via `#ukbatch-arrow path`
// CSS — so it stays the SAME colour/weight as the stroke. Idempotent (re-setting the same attr is a
// no-op). Runs inside the st.syncing guard, so it never triggers connectionCreated re-entrancy.
function applyArrowMarkers(st) {
    const paths = st.editor.container.querySelectorAll('.connection .main-path');
    for (const p of paths) p.setAttribute('marker-end', 'url(#ukbatch-arrow)');
}

// CSS.escape guards an attribute selector built from a StepId; falls back to a conservative manual
// escape on the (vanishingly rare) engines without CSS.escape so the querySelector never throws.
function cssEscape(s) {
    if (typeof CSS !== 'undefined' && typeof CSS.escape === 'function') return CSS.escape(String(s));
    return String(s).replace(/["'\\\]\[]/g, '\\$&');
}

export function dispose(containerEl) {
    const st = _state.get(containerEl);
    if (!st) return;
    if (st.onDragOver) containerEl.removeEventListener('dragover', st.onDragOver);
    if (st.onDrop) containerEl.removeEventListener('drop', st.onDrop);
    // Symmetric teardown of the Edit-button listeners. The capture flag (3rd arg `true`) MUST match the
    // addEventListener flag or removeEventListener is a no-op (capture is part of the listener identity).
    if (st.onEditPointerDown) {
        containerEl.removeEventListener('pointerdown', st.onEditPointerDown, true);
        containerEl.removeEventListener('mousedown', st.onEditPointerDown, true);
    }
    if (st.onEditClick) containerEl.removeEventListener('click', st.onEditClick);
    for (const h of st.moveTimers.values()) clearTimeout(h);
    st.suppressRemovedEvent = true;
    try { st.editor.clear(); } catch { /* already torn down */ }
    _state.delete(containerEl);
}
