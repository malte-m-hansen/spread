// Parity test: GNOME Shell's overview layout (transcribed from js/ui/workspace.js) vs Spread's C# port.
// Usage: node tools/parity.js [path-to-Spread.dll]
const fs = require('fs');
const path = require('path');
const {spawnSync} = require('child_process');

// ---------- GNOME Shell source (transcribed verbatim; only GObject/Params plumbing removed) ----------
const WINDOW_PREVIEW_MAXIMUM_SCALE = 0.95;
const LAYOUT_SCALE_WEIGHT = 1;
const LAYOUT_SPACE_WEIGHT = 0.1;
const lerp = (start, end, progress) => start + (end - start) * progress;   // misc/util.js

class UnalignedLayoutStrategy {
    constructor({monitor, rowSpacing, columnSpacing}) {
        this._monitor = monitor;
        this._rowSpacing = rowSpacing;
        this._columnSpacing = columnSpacing;
    }

    _newRow() {
        return {x: 0, y: 0, width: 0, height: 0, fullWidth: 0, fullHeight: 0, windows: []};
    }

    _computeWindowScale(window) {
        const ratio = window.boundingBox.height / this._monitor.height;
        return lerp(1.5, 1, ratio);
    }

    _computeRowSizes(layout) {
        const {rows, scale} = layout;
        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            row.width = row.fullWidth * scale + (row.windows.length - 1) * this._columnSpacing;
            row.height = row.fullHeight * scale;
        }
    }

    _keepSameRow(row, window, width, idealRowWidth) {
        if (row.fullWidth + width <= idealRowWidth)
            return true;
        const oldRatio = row.fullWidth / idealRowWidth;
        const newRatio = (row.fullWidth + width) / idealRowWidth;
        if (Math.abs(1 - newRatio) < Math.abs(1 - oldRatio))
            return true;
        return false;
    }

    _sortRow(row) {
        row.windows.sort((a, b) => a.windowCenter.x - b.windowCenter.x);
    }

    computeLayout(windows, layoutParams) {
        const numRows = layoutParams.numRows;
        const rows = [];
        let totalWidth = 0;
        for (let i = 0; i < windows.length; i++) {
            const window = windows[i];
            const s = this._computeWindowScale(window);
            totalWidth += window.boundingBox.width * s;
        }
        const idealRowWidth = totalWidth / numRows;
        const sortedWindows = windows.slice();
        sortedWindows.sort((a, b) => a.windowCenter.y - b.windowCenter.y);
        let windowIdx = 0;
        for (let i = 0; i < numRows; i++) {
            const row = this._newRow();
            rows.push(row);
            for (; windowIdx < sortedWindows.length; windowIdx++) {
                const window = sortedWindows[windowIdx];
                const s = this._computeWindowScale(window);
                const width = window.boundingBox.width * s;
                const height = window.boundingBox.height * s;
                row.fullHeight = Math.max(row.fullHeight, height);
                if (this._keepSameRow(row, window, width, idealRowWidth) || (i === numRows - 1)) {
                    row.windows.push(window);
                    row.fullWidth += width;
                } else {
                    break;
                }
            }
        }
        let gridHeight = 0;
        let maxRow;
        for (let i = 0; i < numRows; i++) {
            const row = rows[i];
            this._sortRow(row);
            if (!maxRow || row.fullWidth > maxRow.fullWidth)
                maxRow = row;
            gridHeight += row.fullHeight;
        }
        return {numRows, rows, maxColumns: maxRow.windows.length, gridWidth: maxRow.fullWidth, gridHeight};
    }

    computeScaleAndSpace(layout, area) {
        const hspacing = (layout.maxColumns - 1) * this._columnSpacing;
        const vspacing = (layout.numRows - 1) * this._rowSpacing;
        const spacedWidth = area.width - hspacing;
        const spacedHeight = area.height - vspacing;
        const horizontalScale = spacedWidth / layout.gridWidth;
        const verticalScale = spacedHeight / layout.gridHeight;
        const scale = Math.min(horizontalScale, verticalScale, WINDOW_PREVIEW_MAXIMUM_SCALE);
        const scaledLayoutWidth = layout.gridWidth * scale + hspacing;
        const scaledLayoutHeight = layout.gridHeight * scale + vspacing;
        const space = (scaledLayoutWidth * scaledLayoutHeight) / (area.width * area.height);
        layout.scale = scale;
        return [scale, space];
    }

    computeWindowSlots(layout, area) {
        this._computeRowSizes(layout);
        const {rows, scale} = layout;
        const slots = [];
        let heightWithoutSpacing = 0;
        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            heightWithoutSpacing += row.height;
        }
        const verticalSpacing = (rows.length - 1) * this._rowSpacing;
        const additionalVerticalScale = Math.min(1, (area.height - verticalSpacing) / heightWithoutSpacing);
        let compensation = 0;
        let y = 0;
        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            const horizontalSpacing = (row.windows.length - 1) * this._columnSpacing;
            const widthWithoutSpacing = row.width - horizontalSpacing;
            const additionalHorizontalScale = Math.min(1, (area.width - horizontalSpacing) / widthWithoutSpacing);
            if (additionalHorizontalScale < additionalVerticalScale) {
                row.additionalScale = additionalHorizontalScale;
                compensation += (additionalVerticalScale - additionalHorizontalScale) * row.height;
            } else {
                row.additionalScale = additionalVerticalScale;
            }
            row.x = area.x + (Math.max(area.width - (widthWithoutSpacing * row.additionalScale + horizontalSpacing), 0) / 2);
            row.y = area.y + (Math.max(area.height - (heightWithoutSpacing + verticalSpacing), 0) / 2) + y;
            y += row.height * row.additionalScale + this._rowSpacing;
        }
        compensation /= 2;
        for (let i = 0; i < rows.length; i++) {
            const row = rows[i];
            const rowY = row.y + compensation;
            const rowHeight = row.height * row.additionalScale;
            let x = row.x;
            for (let j = 0; j < row.windows.length; j++) {
                const window = row.windows[j];
                let s = scale * this._computeWindowScale(window) * row.additionalScale;
                const cellWidth = window.boundingBox.width * s;
                const cellHeight = window.boundingBox.height * s;
                s = Math.min(s, WINDOW_PREVIEW_MAXIMUM_SCALE);
                const cloneWidth = window.boundingBox.width * s;
                const cloneHeight = window.boundingBox.height * s;
                let cloneX = x + (cellWidth - cloneWidth) / 2;
                let cloneY;
                if (rows.length === 1)
                    cloneY = rowY + (rowHeight - cloneHeight) / 2;
                else
                    cloneY = rowY + rowHeight - cellHeight;
                cloneX = Math.floor(cloneX);
                cloneY = Math.floor(cloneY);
                slots.push([cloneX, cloneY, cloneWidth, cloneHeight, window]);
                x += cellWidth + this._columnSpacing;
            }
        }
        return slots;
    }
}

function isBetterScaleAndSpace(oldScale, oldSpace, scale, space) {
    const spacePower = (space - oldSpace) * LAYOUT_SPACE_WEIGHT;
    const scalePower = (scale - oldScale) * LAYOUT_SCALE_WEIGHT;
    if (scale > oldScale && space > oldSpace) return true;
    else if (scale > oldScale && space <= oldSpace) return scalePower > spacePower;
    else if (scale <= oldScale && space > oldSpace) return spacePower > scalePower;
    else return false;
}

// WorkspaceLayout._createBestLayout + _getWindowSlots, with the strategy built exactly like the original
function gnomeSlots(windows, monitor, area, rowSpacing, colSpacing) {
    const strategy = new UnalignedLayoutStrategy({monitor, rowSpacing, columnSpacing: colSpacing});
    let lastLayout = null, lastNumColumns = -1, lastScale = 0, lastSpace = 0;
    const availArea = {x: parseInt(area.x), y: parseInt(area.y), width: parseInt(area.w), height: parseInt(area.h)};
    for (let numRows = 1; ; numRows++) {
        const numColumns = Math.ceil(windows.length / numRows);
        if (numColumns === lastNumColumns) break;
        const layout = strategy.computeLayout(windows, {numRows});
        const [scale, space] = strategy.computeScaleAndSpace(layout, availArea);
        if (lastLayout && !isBetterScaleAndSpace(lastScale, lastSpace, scale, space)) break;
        lastLayout = layout; lastNumColumns = numColumns; lastScale = scale; lastSpace = space;
    }
    return strategy.computeWindowSlots(lastLayout, availArea);
}

// ---------- test driver ----------
function mulberry32(a) { return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }
const rnd = mulberry32(12345);
const ri = (a, b) => Math.floor(a + rnd() * (b - a));

const cases = [];
const monitors = [{w: 3413, h: 1440}, {w: 1920, h: 1080}, {w: 2560, h: 1440}];
for (let c = 0; c < 400; c++) {
    const mon = monitors[c % monitors.length];
    const n = c < 12 ? c + 1 : ri(1, 21);                     // exercise 1..12 windows explicitly
    const scale = [1, 1.25, 1.5, 2][c % 4];
    const spacing = (20 + (1 - 0.7) * 64 + 5) * scale;
    const windows = Array.from({length: n}, () => {
        const w = ri(220, mon.w), h = ri(160, mon.h);
        return {x: ri(0, Math.max(1, mon.w - w)), y: ri(0, Math.max(1, mon.h - h)), w, h};
    });
    cases.push({monitor: mon, area: {x: 80 * scale, y: 110 * scale, w: mon.w - 160 * scale, h: mon.h - 166 * scale},
                rowSpacing: spacing, colSpacing: spacing, windows});
}
// hand-made: ties on centre.y/centre.x (JS sort is stable), identical windows, a single tiny window
cases.push({monitor: monitors[0], area: {x: 80, y: 110, w: 3253, h: 1274}, rowSpacing: 44.2, colSpacing: 44.2,
            windows: [{x: 0, y: 0, w: 800, h: 600}, {x: 900, y: 0, w: 800, h: 600}, {x: 1800, y: 0, w: 800, h: 600}, {x: 0, y: 700, w: 800, h: 600}]});
cases.push({monitor: monitors[0], area: {x: 80, y: 110, w: 3253, h: 1274}, rowSpacing: 44.2, colSpacing: 44.2, windows: [{x: 100, y: 100, w: 300, h: 200}]});

const expected = cases.map(c => {
    const wins = c.windows.map(w => ({boundingBox: {x: w.x, y: w.y, width: w.w, height: w.h},
                                      windowCenter: {x: w.x + w.w / 2, y: w.y + w.h / 2}}));
    const slots = gnomeSlots(wins, {width: c.monitor.w, height: c.monitor.h}, c.area, c.rowSpacing, c.colSpacing);
    const out = new Array(wins.length);
    for (const [x, y, w, h, win] of slots) out[wins.indexOf(win)] = {x, y, w, h};
    return out;
});

const dll = process.argv[2] || path.join(__dirname, '..', 'bin', 'Release', 'net8.0-windows', 'Spread.dll');
const casesFile = path.join(__dirname, 'cases.json');
fs.writeFileSync(casesFile, JSON.stringify({cases}));
const r = spawnSync('dotnet', [dll, '--layout-dump', casesFile], {encoding: 'utf8', maxBuffer: 1 << 28});
if (r.status !== 0) { console.error('dotnet failed:', r.stderr || r.error); process.exit(2); }
const actual = JSON.parse(r.stdout).results;

let bad = 0, worst = 0;
cases.forEach((c, i) => {
    const e = expected[i], a = actual[i];
    for (let k = 0; k < e.length; k++) {
        const d = Math.max(Math.abs(e[k].x - a[k].x), Math.abs(e[k].y - a[k].y), Math.abs(e[k].w - a[k].w), Math.abs(e[k].h - a[k].h));
        worst = Math.max(worst, d);
        if (!(d < 1e-6)) { bad++; if (bad <= 5) console.log(`MISMATCH case ${i} (n=${e.length}) window ${k}:`, e[k], a[k]); }
    }
});
const total = expected.reduce((s, e) => s + e.length, 0);
console.log(bad === 0 ? `PARITY OK: ${cases.length} cases, ${total} window slots identical (max diff ${worst})`
                      : `PARITY FAILED: ${bad}/${total} slots differ (max diff ${worst})`);
process.exit(bad === 0 ? 0 : 1);
