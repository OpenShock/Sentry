// Region editor canvas overlay — handles drag to move/resize detection regions.
// Regions use normalized coordinates (0–1). The canvas is sized to match the preview image.

const HANDLE_SIZE = 8;
const EDGE_THRESHOLD = 10;

const editors = new Map();

export function initialize(canvasId, dotnetRef, regions, colors) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    const ctx = canvas.getContext('2d');

    const state = {
        canvas,
        ctx,
        dotnetRef,
        regions: regions || [],
        colors: colors || [],
        selectedIndex: -1,
        dragType: null,   // 'move' | 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw'
        dragStartX: 0,
        dragStartY: 0,
        dragStartRegion: null,
        hoveredIndex: -1,
        hoveredEdge: null,
    };

    editors.set(canvasId, state);

    canvas.addEventListener('mousedown', e => onMouseDown(state, e));
    canvas.addEventListener('mousemove', e => onMouseMove(state, e));
    canvas.addEventListener('mouseup', e => onMouseUp(state, e));
    canvas.addEventListener('mouseleave', e => onMouseUp(state, e));

    draw(state);
}

export function updateRegions(canvasId, regions, colors) {
    const state = editors.get(canvasId);
    if (!state) return;
    state.regions = regions || [];
    state.colors = colors || [];
    draw(state);
}

export function syncCanvasSize(canvasId) {
    const state = editors.get(canvasId);
    if (!state) return;
    const parent = state.canvas.parentElement;
    const img = parent?.querySelector('img');
    if (img && img.naturalWidth > 0) {
        state.canvas.width = img.clientWidth;
        state.canvas.height = img.clientHeight;
        draw(state);
    }
}

export function dispose(canvasId) {
    editors.delete(canvasId);
}

function getMousePos(state, e) {
    const rect = state.canvas.getBoundingClientRect();
    return {
        x: (e.clientX - rect.left) / state.canvas.width,
        y: (e.clientY - rect.top) / state.canvas.height
    };
}

function hitTest(state, pos) {
    // Check regions in reverse order (topmost first)
    for (let i = state.regions.length - 1; i >= 0; i--) {
        const r = state.regions[i];
        const edge = getEdge(r, pos);
        if (edge) return { index: i, edge };
    }
    // Check interior
    for (let i = state.regions.length - 1; i >= 0; i--) {
        const r = state.regions[i];
        if (pos.x >= r.x && pos.x <= r.x + r.width &&
            pos.y >= r.y && pos.y <= r.y + r.height) {
            return { index: i, edge: 'move' };
        }
    }
    return null;
}

function getEdge(r, pos) {
    const t = EDGE_THRESHOLD / 1000; // normalized threshold (approximate)
    const onLeft   = Math.abs(pos.x - r.x) < t && pos.y >= r.y - t && pos.y <= r.y + r.height + t;
    const onRight  = Math.abs(pos.x - (r.x + r.width)) < t && pos.y >= r.y - t && pos.y <= r.y + r.height + t;
    const onTop    = Math.abs(pos.y - r.y) < t && pos.x >= r.x - t && pos.x <= r.x + r.width + t;
    const onBottom = Math.abs(pos.y - (r.y + r.height)) < t && pos.x >= r.x - t && pos.x <= r.x + r.width + t;

    if (onTop && onLeft) return 'nw';
    if (onTop && onRight) return 'ne';
    if (onBottom && onLeft) return 'sw';
    if (onBottom && onRight) return 'se';
    if (onTop) return 'n';
    if (onBottom) return 's';
    if (onLeft) return 'w';
    if (onRight) return 'e';
    return null;
}

function getCursor(edge) {
    switch (edge) {
        case 'n': case 's': return 'ns-resize';
        case 'e': case 'w': return 'ew-resize';
        case 'nw': case 'se': return 'nwse-resize';
        case 'ne': case 'sw': return 'nesw-resize';
        case 'move': return 'move';
        default: return 'default';
    }
}

function onMouseDown(state, e) {
    const pos = getMousePos(state, e);
    const hit = hitTest(state, pos);

    if (hit) {
        state.selectedIndex = hit.index;
        state.dragType = hit.edge;
        state.dragStartX = pos.x;
        state.dragStartY = pos.y;
        state.dragStartRegion = { ...state.regions[hit.index] };
        e.preventDefault();
    } else {
        state.selectedIndex = -1;
        state.dragType = null;
    }
    draw(state);
    state.dotnetRef.invokeMethodAsync('OnRegionSelected', state.selectedIndex);
}

function onMouseMove(state, e) {
    const pos = getMousePos(state, e);

    if (state.dragType && state.dragStartRegion) {
        const dx = pos.x - state.dragStartX;
        const dy = pos.y - state.dragStartY;
        const orig = state.dragStartRegion;
        const r = state.regions[state.selectedIndex];

        switch (state.dragType) {
            case 'move':
                r.x = clamp(orig.x + dx, 0, 1 - orig.width);
                r.y = clamp(orig.y + dy, 0, 1 - orig.height);
                break;
            case 'n':
                r.y = clamp(orig.y + dy, 0, orig.y + orig.height - 0.01);
                r.height = orig.y + orig.height - r.y;
                break;
            case 's':
                r.height = clamp(orig.height + dy, 0.01, 1 - orig.y);
                break;
            case 'w':
                r.x = clamp(orig.x + dx, 0, orig.x + orig.width - 0.01);
                r.width = orig.x + orig.width - r.x;
                break;
            case 'e':
                r.width = clamp(orig.width + dx, 0.01, 1 - orig.x);
                break;
            case 'nw':
                r.x = clamp(orig.x + dx, 0, orig.x + orig.width - 0.01);
                r.y = clamp(orig.y + dy, 0, orig.y + orig.height - 0.01);
                r.width = orig.x + orig.width - r.x;
                r.height = orig.y + orig.height - r.y;
                break;
            case 'ne':
                r.y = clamp(orig.y + dy, 0, orig.y + orig.height - 0.01);
                r.width = clamp(orig.width + dx, 0.01, 1 - orig.x);
                r.height = orig.y + orig.height - r.y;
                break;
            case 'sw':
                r.x = clamp(orig.x + dx, 0, orig.x + orig.width - 0.01);
                r.width = orig.x + orig.width - r.x;
                r.height = clamp(orig.height + dy, 0.01, 1 - orig.y);
                break;
            case 'se':
                r.width = clamp(orig.width + dx, 0.01, 1 - orig.x);
                r.height = clamp(orig.height + dy, 0.01, 1 - orig.y);
                break;
        }
        draw(state);
    } else {
        // Hover cursor
        const hit = hitTest(state, pos);
        state.canvas.style.cursor = hit ? getCursor(hit.edge) : 'default';
        const newHover = hit ? hit.index : -1;
        if (newHover !== state.hoveredIndex) {
            state.hoveredIndex = newHover;
            draw(state);
        }
    }
}

function onMouseUp(state, e) {
    if (state.dragType && state.selectedIndex >= 0) {
        const r = state.regions[state.selectedIndex];
        state.dotnetRef.invokeMethodAsync('OnRegionChanged', state.selectedIndex,
            round(r.x), round(r.y), round(r.width), round(r.height));
    }
    state.dragType = null;
    state.dragStartRegion = null;
}

function draw(state) {
    const { ctx, canvas, regions, colors, selectedIndex, hoveredIndex } = state;
    ctx.clearRect(0, 0, canvas.width, canvas.height);

    for (let i = 0; i < regions.length; i++) {
        const r = regions[i];
        const color = colors[i] || '#00c8ff';
        const isSelected = i === selectedIndex;
        const isHovered = i === hoveredIndex;

        const px = r.x * canvas.width;
        const py = r.y * canvas.height;
        const pw = r.width * canvas.width;
        const ph = r.height * canvas.height;

        // Fill
        ctx.fillStyle = color + (isSelected ? '33' : '18');
        ctx.fillRect(px, py, pw, ph);

        // Border
        ctx.strokeStyle = color;
        ctx.lineWidth = isSelected ? 3 : (isHovered ? 2.5 : 1.5);
        if (isSelected) ctx.setLineDash([]);
        else ctx.setLineDash([6, 3]);
        ctx.strokeRect(px, py, pw, ph);
        ctx.setLineDash([]);

        // Corner handles (only for selected)
        if (isSelected) {
            const hs = HANDLE_SIZE;
            ctx.fillStyle = color;
            // corners
            ctx.fillRect(px - hs/2, py - hs/2, hs, hs);
            ctx.fillRect(px + pw - hs/2, py - hs/2, hs, hs);
            ctx.fillRect(px - hs/2, py + ph - hs/2, hs, hs);
            ctx.fillRect(px + pw - hs/2, py + ph - hs/2, hs, hs);
            // edge midpoints
            ctx.fillRect(px + pw/2 - hs/2, py - hs/2, hs, hs);
            ctx.fillRect(px + pw/2 - hs/2, py + ph - hs/2, hs, hs);
            ctx.fillRect(px - hs/2, py + ph/2 - hs/2, hs, hs);
            ctx.fillRect(px + pw - hs/2, py + ph/2 - hs/2, hs, hs);
        }

        // Label
        ctx.font = '12px system-ui, sans-serif';
        ctx.fillStyle = color;
        ctx.shadowColor = '#000';
        ctx.shadowBlur = 3;
        const label = r.label || `Region ${i + 1}`;
        ctx.fillText(label, px + 4, py - 6);
        ctx.shadowBlur = 0;
    }
}

function clamp(v, min, max) { return Math.max(min, Math.min(max, v)); }
function round(v) { return Math.round(v * 10000) / 10000; }