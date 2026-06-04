// UKBatch.Dashboard DAG canvas — ES module.
// Imported via IJSObjectReference in DagView.OnAfterRenderAsync(firstRender). Pure DOM-side:
// applies the CSS transform locally for 60fps pan/zoom WITHOUT a server round-trip per wheel tick,
// then notifies .NET (debounced) so C# state + the % indicator stay authoritative.
//
// OPTIONAL ENHANCEMENT: the toolbar +/-/reset buttons + the inline transform from C# state work with
// ZERO JS (graceful degradation). This module only adds wheel-zoom + drag-pan. If it 404s / fails to
// import, DagView catches the JSException and leaves the page fully usable.

const _handlers = new WeakMap();

export function init(canvasEl, svgEl, dotnetRef, options) {
    // options: { minZoom, maxZoom, step }
    const state = { zoom: 1, panX: 0, panY: 0, dragging: false, lastX: 0, lastY: 0, raf: 0 };

    function apply() {
        svgEl.style.transform = `scale(${state.zoom}) translate(${state.panX}px, ${state.panY}px)`;
    }

    let notifyTimer = 0;
    function notify() {
        clearTimeout(notifyTimer);
        notifyTimer = setTimeout(() => {
            // ONE-WAY push to C# so the % indicator + ResetView stay correct. Fire-and-forget.
            dotnetRef.invokeMethodAsync('OnViewChangedFromJs', state.zoom, state.panX, state.panY);
        }, 120);
    }

    const onWheel = (e) => {
        e.preventDefault();
        const delta = e.deltaY > 0 ? -options.step : options.step;
        state.zoom = Math.min(Math.max(options.minZoom, state.zoom + delta), options.maxZoom);
        apply(); notify();
    };
    const onDown = (e) => {
        state.dragging = true;
        state.lastX = e.clientX; state.lastY = e.clientY;
        canvasEl.classList.add('dag-canvas--grabbing');
    };
    const onUp = () => {
        state.dragging = false;
        canvasEl.classList.remove('dag-canvas--grabbing');
    };
    const onMove = (e) => {
        if (!state.dragging) return;
        state.panX += (e.clientX - state.lastX) / state.zoom;
        state.panY += (e.clientY - state.lastY) / state.zoom;
        state.lastX = e.clientX; state.lastY = e.clientY;
        if (!state.raf) state.raf = requestAnimationFrame(() => { apply(); state.raf = 0; });
        notify();
    };

    canvasEl.addEventListener('wheel', onWheel, { passive: false });
    canvasEl.addEventListener('mousedown', onDown);
    window.addEventListener('mouseup', onUp);
    window.addEventListener('mousemove', onMove);
    _handlers.set(canvasEl, { onWheel, onDown, onUp, onMove });

    // Allow C# (toolbar buttons / ResetView) to drive the same transform.
    return {
        setView: (zoom, panX, panY) => { state.zoom = zoom; state.panX = panX; state.panY = panY; apply(); },
    };
}

export function dispose(canvasEl) {
    const h = _handlers.get(canvasEl);
    if (!h) return;
    canvasEl.removeEventListener('wheel', h.onWheel);
    canvasEl.removeEventListener('mousedown', h.onDown);
    window.removeEventListener('mouseup', h.onUp);
    window.removeEventListener('mousemove', h.onMove);
    _handlers.delete(canvasEl);
}
