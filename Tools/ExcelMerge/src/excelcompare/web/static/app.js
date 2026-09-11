const state = {
    diff: null,
    resolutions: {},
    keepBothEdits: {},
    currentSheet: null,
    selectedCell: null,
    undoStack: [],
};

function colToLetter(col) {
    let result = "";
    while (col > 0) {
        const rem = (col - 1) % 26;
        result = String.fromCharCode(65 + rem) + result;
        col = Math.floor((col - 1) / 26);
    }
    return result;
}

function cellDisplay(cv) {
    if (!cv) return "(empty)";
    if (cv.formula) return cv.formula;
    if (cv.value === null || cv.value === undefined) return "(empty)";
    return String(cv.value);
}

function resolutionKey(sheet, row, col) {
    return `${sheet}:${row},${col}`;
}

function pushUndo(label, keys) {
    const snapshot = {};
    for (const rKey of keys) {
        snapshot[rKey] = state.resolutions[rKey] !== undefined
            ? JSON.parse(JSON.stringify(state.resolutions[rKey]))
            : null;
    }
    state.undoStack.push({ label, snapshot });
    updateUndoButton();
}

function updateUndoButton() {
    const btn = document.getElementById("btn-undo");
    if (!btn) return;
    if (state.undoStack.length > 0) {
        const last = state.undoStack[state.undoStack.length - 1];
        btn.disabled = false;
        btn.title = `撤销: ${last.label}`;
    } else {
        btn.disabled = true;
        btn.title = "没有可撤销的操作";
    }
}

async function undo() {
    if (state.undoStack.length === 0) return;
    const entry = state.undoStack.pop();

    const toRestore = [];
    const toRemove = [];

    for (const [rKey, oldVal] of Object.entries(entry.snapshot)) {
        if (oldVal === null) {
            toRemove.push(rKey);
            delete state.resolutions[rKey];
        } else {
            state.resolutions[rKey] = oldVal;
            const [sheetPart, coords] = rKey.split(":");
            const [row, col] = coords.split(",").map(Number);
            const choice = typeof oldVal === "object" ? oldVal.choice : oldVal;
            const manual_value = typeof oldVal === "object" ? oldVal.value : undefined;
            toRestore.push({ sheet: sheetPart, row, col, choice, manual_value });
        }
    }

    try {
        if (toRemove.length > 0) {
            const unresolveItems = toRemove.map(rKey => {
                const [sheetPart, coords] = rKey.split(":");
                const [row, col] = coords.split(",").map(Number);
                return { sheet: sheetPart, row, col };
            });
            await fetch("/api/unresolve", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ items: unresolveItems }),
            });
        }
        if (toRestore.length > 0) {
            await fetch("/api/resolve", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ resolutions: toRestore }),
            });
        }
    } catch (e) {
        showToast(`撤销失败: ${e.message}`);
        console.error("undo error:", e);
        return;
    }

    renderGrid(state.currentSheet);
    renderSheetTabs();
    updateStatus();
    updateUndoButton();
    showToast(`已撤销: ${entry.label}`);
}

async function loadDiff() {
    try {
        const resp = await fetch("/api/diff");
        state.diff = await resp.json();

        if (state.diff.resolutions) {
            for (const [key, choice] of Object.entries(state.diff.resolutions)) {
                state.resolutions[key] = choice;
            }
        }

        renderSheetTabs();
        if (state.diff.sheet_order.length > 0) {
            switchSheet(state.diff.sheet_order[0]);
        }
        updateStatus();
    } catch (e) {
        showToast(`Error loading diff: ${e.message}`);
        console.error("loadDiff error:", e);
    }
}

function _needsResolution(kind) {
    return kind !== "unchanged" && kind !== "both_same";
}

function updateStatus() {
    const info = document.getElementById("conflict-info");
    let totalConflicts = state.diff.conflict_count;
    let resolved = 0;
    for (const [key, choice] of Object.entries(state.resolutions)) {
        const parts = key.split(":");
        const sheet = parts[0];
        const [row, col] = parts[1].split(",").map(Number);
        const sd = state.diff.sheets[sheet];
        if (sd && sd.cells[`${row},${col}`] && _needsResolution(sd.cells[`${row},${col}`].kind)) {
            resolved++;
        }
    }
    const remaining = totalConflicts - resolved;

    let html = remaining > 0
        ? `<span class="conflict-link" onclick="jumpToNextConflict()">待确认: <b>${remaining}</b> 未解决 / ${totalConflicts} 总计 ▶</span>`
        : `<span>全部已解决 (${totalConflicts} 总计)</span>`;
    html += `<span class="legend">`;
    html += `<span class="legend-item"><span class="legend-swatch" style="background:#d5f5e3"></span>Ours</span>`;
    html += `<span class="legend-item"><span class="legend-swatch" style="background:#d6eaf8"></span>Theirs</span>`;
    html += `<span class="legend-item"><span class="legend-swatch" style="background:#e6b0aa"></span>冲突</span>`;
    html += `<span class="legend-item"><span class="legend-swatch" style="background:#fdebd0"></span>已解决</span>`;
    html += `<span class="legend-item"><span class="legend-swatch" style="background:#e8daef"></span>保留双方</span>`;
    html += `</span>`;
    info.innerHTML = html;
}

function renderSheetTabs() {
    const nav = document.getElementById("sheet-tabs");
    nav.innerHTML = "";
    for (const name of state.diff.sheet_order) {
        const sd = state.diff.sheets[name];
        if (!sd) continue;
        const tab = document.createElement("div");
        tab.className = "sheet-tab" + (name === state.currentSheet ? " active" : "");
        tab.onclick = () => switchSheet(name);

        let label = name;
        tab.innerHTML = label;

        if (sd.conflict_count > 0) {
            const unresolvedInSheet = countUnresolvedInSheet(name);
            const badge = document.createElement("span");
            badge.className = "tab-badge" + (unresolvedInSheet === 0 ? " resolved" : "");
            badge.textContent = unresolvedInSheet > 0 ? unresolvedInSheet : "✓";
            tab.appendChild(badge);
        }
        nav.appendChild(tab);
    }
}

function countUnresolvedInSheet(sheetName) {
    const sd = state.diff.sheets[sheetName];
    if (!sd) return 0;
    let count = 0;
    for (const [key, cd] of Object.entries(sd.cells)) {
        if (_needsResolution(cd.kind)) {
            const realKey = `${sheetName}:${key}`;
            if (!state.resolutions[realKey]) {
                count++;
            }
        }
    }
    return count;
}

function switchSheet(name) {
    state.currentSheet = name;
    state.selectedCell = null;
    document.getElementById("cell-detail").classList.add("hidden");
    renderSheetTabs();
    renderGrid(name);
    updateKeyColSelector(name);
}

function _getRowSource(rm) {
    if (rm.base_row == null && rm.ours_row != null && rm.theirs_row == null) return "ours_only";
    if (rm.base_row == null && rm.theirs_row != null && rm.ours_row == null) return "theirs_only";
    return null;
}

function _isRowHiddenByBulk(sheetName, bulkChoice) {
    const sd = state.diff.sheets[sheetName];
    if (!sd) return false;
    for (const [key, cd] of Object.entries(sd.cells)) {
        if (cd.kind === "conflict") {
            const realKey = `${sheetName}:${key}`;
            const res = state.resolutions[realKey];
            if (!res) return false;
            const choice = typeof res === "object" ? res.choice : res;
            if (choice !== bulkChoice) return false;
        }
    }
    const hasConflicts = Object.values(sd.cells).some(cd => cd.kind === "conflict");
    return hasConflicts;
}

function _renderKeepBothRows(tbody, sd, sheetName, r, maxCol) {
    const rm = sd.row_mapping ? sd.row_mapping.find(m => m.virtual_row === r) : null;

    for (const side of ["ours", "theirs"]) {
        const tr = document.createElement("tr");
        tr.className = side === "ours" ? "row-keep-both-ours" : "row-keep-both-theirs";

        const rowHeader = document.createElement("td");
        rowHeader.className = "row-header";
        const badge = side === "ours" ? "O" : "T";
        const badgeClass = side === "ours" ? "row-source-ours" : "row-source-theirs";
        rowHeader.innerHTML = `<span class="row-source-badge ${badgeClass}">${badge}</span>${r}`;
        tr.appendChild(rowHeader);

        for (let c = 1; c <= maxCol; c++) {
            const td = document.createElement("td");
            td.setAttribute("data-vrow", r);
            td.setAttribute("data-vcol", c);
            td.setAttribute("data-side", side);
            const cellKey = `${r},${c}`;
            const cd = sd.cells[cellKey];

            const editKey = `${sheetName}:${r},${c}:${side}`;
            const edited = state.keepBothEdits[editKey];

            if (edited !== undefined) {
                td.textContent = edited;
                td.style.cursor = "pointer";
                td.ondblclick = () => _startInlineEdit(td, sheetName, r, c, side);
            } else if (cd && cd.kind !== "unchanged") {
                td.style.cursor = "pointer";
                const cv = side === "ours" ? (cd.ours || cd.base) : (cd.theirs || cd.base);
                td.textContent = cellDisplay(cv);
                td.ondblclick = () => _startInlineEdit(td, sheetName, r, c, side);
                td.onclick = () => selectCell(sheetName, r, c, cd);
            } else if (cd) {
                const cv = side === "ours" ? (cd.ours || cd.base) : (cd.theirs || cd.base);
                td.textContent = cellDisplay(cv);
            }

            if (state.selectedCell &&
                state.selectedCell.row === r &&
                state.selectedCell.col === c) {
                td.classList.add("cell-selected");
            }

            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
}

function _startInlineEditMain(td, sheetName, row, col, cd) {
    if (td.querySelector("input")) return;
    const currentText = td.textContent;
    const original = cd ? cellDisplay(cd.ours || cd.theirs || cd.base) : currentText;
    const input = document.createElement("input");
    input.type = "text";
    input.value = original === "(empty)" ? "" : original;
    input.className = "inline-edit-input";

    td.textContent = "";
    td.appendChild(input);
    input.focus();
    input.select();

    const commit = async () => {
        const newVal = input.value;
        td.textContent = newVal || "(empty)";
        const rKey = resolutionKey(sheetName, row, col);
        pushUndo(`${colToLetter(col)}${row} 编辑`, [rKey]);
        try {
            await fetch("/api/resolve", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({
                    resolutions: [{ sheet: sheetName, row, col, choice: "manual", manual_value: newVal }]
                }),
            });
            state.resolutions[rKey] = { choice: "manual", value: newVal };
            renderGrid(state.currentSheet);
            renderSheetTabs();
            updateStatus();
            showToast(`${colToLetter(col)}${row}: 已编辑`);
        } catch (e) {
            showToast(`Error: ${e.message}`);
        }
    };

    let committed = false;
    input.onblur = () => { if (!committed) { committed = true; commit(); } };
    input.onkeydown = (e) => {
        if (e.key === "Enter") { e.preventDefault(); committed = true; commit(); }
        if (e.key === "Escape") { td.textContent = currentText; }
    };
}

function _startInlineEdit(td, sheetName, row, col, side) {
    if (td.querySelector("input")) return;
    const currentText = td.textContent;
    const input = document.createElement("input");
    input.type = "text";
    input.value = currentText === "(empty)" ? "" : currentText;
    input.className = "inline-edit-input";

    td.textContent = "";
    td.appendChild(input);
    input.focus();
    input.select();

    const commit = () => {
        const editKey = `${sheetName}:${row},${col}:${side}`;
        state.keepBothEdits[editKey] = input.value;
        td.textContent = input.value || "(empty)";
        showToast(`${colToLetter(col)}${row} (${side}): 已编辑`);
    };

    input.onblur = commit;
    input.onkeydown = (e) => {
        if (e.key === "Enter") { e.preventDefault(); input.blur(); }
        if (e.key === "Escape") { td.textContent = currentText; }
    };
}

function renderGrid(sheetName) {
    const sd = state.diff.sheets[sheetName];
    if (!sd) return;

    const thead = document.getElementById("grid-head");
    const tbody = document.getElementById("grid-body");
    thead.innerHTML = "";
    tbody.innerHTML = "";

    const maxRow = sd.max_row || 1;
    const maxCol = sd.max_col || 1;

    const headerRow = document.createElement("tr");
    const corner = document.createElement("th");
    corner.textContent = "";
    corner.className = "row-header";
    headerRow.appendChild(corner);
    for (let c = 1; c <= maxCol; c++) {
        const th = document.createElement("th");
        if (sd.col_mapping) {
            const cm = sd.col_mapping.find(m => m.virtual_col === c);
            th.textContent = cm ? cm.header : colToLetter(c);
        } else {
            th.textContent = colToLetter(c);
        }
        headerRow.appendChild(th);
    }
    thead.appendChild(headerRow);

    const rowMap = sd.row_mapping || null;

    for (let r = 1; r <= maxRow; r++) {
        const rm = rowMap ? rowMap.find(m => m.virtual_row === r) : null;
        const rowSource = rm ? _getRowSource(rm) : null;

        if (rowSource === "ours_only" && _isRowHiddenByBulk(sheetName, "theirs")) continue;
        if (rowSource === "theirs_only" && _isRowHiddenByBulk(sheetName, "ours")) continue;

        let hasKeepBoth = false;
        for (let c = 1; c <= maxCol; c++) {
            const rk = resolutionKey(sheetName, r, c);
            const res = state.resolutions[rk];
            if (res && (typeof res === "object" ? res.choice : res) === "keep_both") {
                hasKeepBoth = true;
                break;
            }
        }
        if (hasKeepBoth) {
            _renderKeepBothRows(tbody, sd, sheetName, r, maxCol);
            continue;
        }

        const rowHasChange = Object.keys(sd.cells).some(k => k.startsWith(`${r},`));

        const tr = document.createElement("tr");
        if (rowSource === "ours_only") tr.className = "row-ours-only";
        else if (rowSource === "theirs_only") tr.className = "row-theirs-only";

        const rowHeader = document.createElement("td");
        rowHeader.className = "row-header";
        if (rowSource === "ours_only") {
            rowHeader.innerHTML = `<span class="row-source-badge row-source-ours">O</span>${r}`;
        } else if (rowSource === "theirs_only") {
            rowHeader.innerHTML = `<span class="row-source-badge row-source-theirs">T</span>${r}`;
        } else {
            rowHeader.textContent = r;
        }
        tr.appendChild(rowHeader);

        for (let c = 1; c <= maxCol; c++) {
            const td = document.createElement("td");
            td.setAttribute("data-vrow", r);
            td.setAttribute("data-vcol", c);
            const cellKey = `${r},${c}`;
            const cd = sd.cells[cellKey];
            const rKey = resolutionKey(sheetName, r, c);

            if (cd && cd.kind === "unchanged") {
                td.textContent = cellDisplay(cd.ours || cd.theirs || cd.base);
                td.ondblclick = () => _startInlineEditMain(td, sheetName, r, c, cd);
            } else if (cd) {
                const resolved = state.resolutions[rKey];
                const resChoice = resolved ? (typeof resolved === "object" ? resolved.choice : resolved) : null;

                if (resChoice === "delete") {
                    td.className = "cell-deleted";
                } else if (resChoice === "keep_both") {
                    td.className = "cell-keep-both";
                } else if (resolved) {
                    td.className = "cell-resolved";
                } else if (cd.kind === "conflict") {
                    td.className = "cell-conflict";
                } else if (cd.kind === "ours") {
                    td.className = "cell-ours";
                } else if (cd.kind === "theirs") {
                    td.className = "cell-theirs";
                } else if (cd.kind === "both_same") {
                    td.className = "cell-both-same";
                }

                if (resolved) {
                    const choice = typeof resolved === "object" ? resolved.choice : resolved;
                    if (choice === "delete") {
                        td.textContent = "(deleted)";
                    } else if (choice === "ours") {
                        td.textContent = cellDisplay(cd.ours);
                    } else if (choice === "theirs") {
                        td.textContent = cellDisplay(cd.theirs);
                    } else if (choice === "keep_both") {
                        td.textContent = `${cellDisplay(cd.ours)} | ${cellDisplay(cd.theirs)}`;
                    } else if (choice === "manual") {
                        const val = typeof resolved === "object" ? resolved.value : null;
                        td.textContent = val !== null && val !== undefined ? val : "(edited)";
                    }
                } else if (cd.kind === "conflict") {
                    td.textContent = `${cellDisplay(cd.ours)} | ${cellDisplay(cd.theirs)}`;
                } else {
                    td.textContent = cellDisplay(cd.ours || cd.theirs || cd.base);
                }

                if (_needsResolution(cd.kind) || state.resolutions[rKey]) {
                    td.onclick = () => selectCell(sheetName, r, c, cd);
                }
                td.ondblclick = () => _startInlineEditMain(td, sheetName, r, c, cd);
            }

            if (state.selectedCell &&
                state.selectedCell.row === r &&
                state.selectedCell.col === c) {
                td.classList.add("cell-selected");
            }

            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
}

function findCellInSource(source, sheet, row, col) {
    return "";
}

function selectCell(sheetName, row, col, cd) {
    state.selectedCell = { sheet: sheetName, row, col, cd };

    document.querySelectorAll(".cell-selected").forEach(el => el.classList.remove("cell-selected"));
    document.querySelectorAll(`td[data-vrow="${row}"][data-vcol="${col}"]`).forEach(el => {
        el.classList.add("cell-selected");
    });

    const panel = document.getElementById("cell-detail");
    panel.classList.remove("hidden");

    document.getElementById("detail-title").textContent =
        `${colToLetter(col)}${row} — ${cd.kind === "conflict" ? "冲突" : cd.kind}`;
    document.getElementById("detail-base").textContent = cellDisplay(cd.base);
    document.getElementById("detail-ours").textContent = cellDisplay(cd.ours);
    document.getElementById("detail-theirs").textContent = cellDisplay(cd.theirs);

    document.getElementById("manual-edit").classList.add("hidden");
    document.getElementById("manual-input").value = cellDisplay(cd.ours);
}

async function resolveSelected(choice) {
    if (!state.selectedCell) return;
    const { sheet, row, col } = state.selectedCell;
    const rKey = resolutionKey(sheet, row, col);
    pushUndo(`${colToLetter(col)}${row} → ${choice}`, [rKey]);

    try {
        const resp = await fetch("/api/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                resolutions: [{ sheet, row, col, choice }]
            }),
        });
        const result = await resp.json();

        const rKey = resolutionKey(sheet, row, col);
        state.resolutions[rKey] = choice;

        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`${colToLetter(col)}${row}: accepted ${choice}`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("resolveSelected error:", e);
    }
}

function showManualEdit() {
    document.getElementById("manual-edit").classList.remove("hidden");
}

async function resolveManual() {
    if (!state.selectedCell) return;
    const { sheet, row, col } = state.selectedCell;
    const manualValue = document.getElementById("manual-input").value;
    const rKey = resolutionKey(sheet, row, col);
    pushUndo(`${colToLetter(col)}${row} → 手动编辑`, [rKey]);

    try {
        const resp = await fetch("/api/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({
                resolutions: [{ sheet, row, col, choice: "manual", manual_value: manualValue }]
            }),
        });
        await resp.json();

        const rKey = resolutionKey(sheet, row, col);
        state.resolutions[rKey] = { choice: "manual", value: manualValue };

        document.getElementById("manual-edit").classList.add("hidden");
        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`${colToLetter(col)}${row}: manually edited`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("resolveManual error:", e);
    }
}

async function bulkResolve(choice, sheet) {
    const affectedKeys = [];
    const sheetNames = sheet ? [sheet] : state.diff.sheet_order;
    for (const sn of sheetNames) {
        const sd = state.diff.sheets[sn];
        if (!sd) continue;
        for (const [key, cd] of Object.entries(sd.cells)) {
            if (_needsResolution(cd.kind)) {
                const [r, c] = key.split(",");
                affectedKeys.push(resolutionKey(sn, r, c));
            }
        }
    }
    pushUndo(`全部选择 ${choice}`, affectedKeys);

    try {
        const resp = await fetch("/api/resolve/bulk", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ sheet: sheet || null, choice }),
        });
        const result = await resp.json();

        for (const sn of sheetNames) {
            const sd = state.diff.sheets[sn];
            if (!sd) continue;
            for (const [key, cd] of Object.entries(sd.cells)) {
                if (_needsResolution(cd.kind)) {
                    const [r, c] = key.split(",");
                    const rKey = resolutionKey(sn, r, c);
                    state.resolutions[rKey] = choice;
                }
            }
        }

        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`All conflicts resolved with: ${choice}`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("bulkResolve error:", e);
    }
}

async function saveMerge() {
    try {
        const resp = await fetch("/api/save", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ keep_both_edits: state.keepBothEdits }),
        });
        const result = await resp.json();

        if (result.ok) {
            if (result.unresolved > 0) {
                showToast(`已保存，但仍有 ${result.unresolved} 个未解决的冲突`);
            } else {
                showToast("合并完成！文件已保存。");
                setTimeout(async () => {
                    await fetch("/api/exit", { method: "POST" }).catch(() => {});
                    window.close();
                }, 1500);
            }
        }
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("saveMerge error:", e);
    }
}

async function cancelMerge() {
    if (!confirm("确定取消合并吗？所有修改将丢失。")) return;
    try {
        await fetch("/api/cancel", { method: "POST" });
        showToast("已取消合并");
        setTimeout(async () => {
            await fetch("/api/exit", { method: "POST" }).catch(() => {});
            window.close();
        }, 1000);
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("cancelMerge error:", e);
    }
}

function showToast(msg) {
    const toast = document.getElementById("toast");
    toast.textContent = msg;
    toast.classList.remove("hidden");
    setTimeout(() => toast.classList.add("hidden"), 2500);
}

function updateKeyColSelector(sheetName) {
    const sd = state.diff.sheets[sheetName];
    if (!sd) return;

    const select = document.getElementById("key-col-select");
    select.innerHTML = '<option value="">-- 按位置 --</option>';

    for (let c = 1; c <= sd.max_col; c++) {
        const opt = document.createElement("option");
        opt.value = c;
        opt.textContent = colToLetter(c);
        select.appendChild(opt);
    }

    if (sd.aligned && sd.key_col) {
        select.value = sd.key_col;
        let info = `已按列 ${colToLetter(sd.key_col)} 对齐行`;
        if (sd.col_aligned) info = `已按表头对齐列，` + info;
        document.getElementById("alignment-info").textContent = info;
    } else {
        select.value = "";
        let info = (sd.aligned && !sd.key_col) ? "已按内容自动对齐行" : "按位置比较";
        if (sd.col_aligned) info = "已按表头对齐列，" + info;
        document.getElementById("alignment-info").textContent = info;
    }
}

async function applyKeyCol() {
    const select = document.getElementById("key-col-select");
    const val = select.value;
    const keyCol = val === "" ? null : parseInt(val, 10);

    try {
        const resp = await fetch("/api/rediff", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ sheet: state.currentSheet, key_col: keyCol }),
        });
        const result = await resp.json();
        if (result.ok) {
            state.diff.sheets[state.currentSheet] = result.sheet;
            // Clear client-side resolutions for this sheet, reload from auto-merge
            for (const key of Object.keys(state.resolutions)) {
                if (key.startsWith(state.currentSheet + ":")) {
                    delete state.resolutions[key];
                }
            }
            // Reload full diff to get updated resolutions
            const diffResp = await fetch("/api/diff");
            const diffData = await diffResp.json();
            if (diffData.resolutions) {
                for (const [key, choice] of Object.entries(diffData.resolutions)) {
                    if (key.startsWith(state.currentSheet + ":")) {
                        state.resolutions[key] = choice;
                    }
                }
            }
            state.diff.conflict_count = diffData.conflict_count;
            renderGrid(state.currentSheet);
            renderSheetTabs();
            updateStatus();
            updateKeyColSelector(state.currentSheet);
            if (result.warning) {
                showToast(result.warning);
            } else {
                showToast(keyCol ? `已按列 ${colToLetter(keyCol)} 重新对齐` : "切换为按位置比较");
            }
        }
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("applyKeyCol error:", e);
    }
}

async function showBranchDiff() {
    if (!state.currentSheet) return;
    try {
        const resp = await fetch(`/api/branch-diff?sheet=${encodeURIComponent(state.currentSheet)}`);
        const data = await resp.json();

        document.getElementById("branch-diff-sheet").textContent = state.currentSheet;
        document.getElementById("branch-diff-modal").classList.remove("hidden");

        const sd = state.diff.sheets[state.currentSheet];
        const maxCol = sd ? sd.max_col : 0;
        const colMap = data.col_mapping;

        const keyCol = data.key_col;
        _renderDiffTable("ours-diff-head", "ours-diff-body", data.ours, maxCol, colMap, keyCol);
        _renderDiffTable("theirs-diff-head", "theirs-diff-body", data.theirs, maxCol, colMap, keyCol);
    } catch (e) {
        showToast(`Error: ${e.message}`);
        console.error("showBranchDiff error:", e);
    }
}

function closeBranchDiff() {
    document.getElementById("branch-diff-modal").classList.add("hidden");
}

function _renderDiffTable(headId, bodyId, diffData, maxCol, colMap, keyCol) {
    const thead = document.getElementById(headId);
    const tbody = document.getElementById(bodyId);
    thead.innerHTML = "";
    tbody.innerHTML = "";

    const maxRow = diffData.max_row || 0;
    const dMaxCol = diffData.max_col || maxCol;

    const headerRow = document.createElement("tr");
    const corner = document.createElement("th");
    corner.textContent = "";
    headerRow.appendChild(corner);
    for (let c = 1; c <= dMaxCol; c++) {
        const th = document.createElement("th");
        if (colMap) {
            const cm = colMap.find(m => m.virtual_col === c);
            th.textContent = cm ? cm.header : colToLetter(c);
        } else {
            th.textContent = colToLetter(c);
        }
        headerRow.appendChild(th);
    }
    thead.appendChild(headerRow);

    for (let r = 1; r <= maxRow; r++) {
        const tr = document.createElement("tr");
        const rowH = document.createElement("td");
        rowH.textContent = r;
        rowH.style.fontWeight = "600";
        rowH.style.textAlign = "center";
        rowH.style.background = "#f8f9fa";
        tr.appendChild(rowH);

        for (let c = 1; c <= dMaxCol; c++) {
            const td = document.createElement("td");
            const cellKey = `${r},${c}`;
            const cd = diffData.cells[cellKey];
            if (cd) {
                if (cd.kind !== "unchanged") {
                    td.className = `diff-${cd.kind}`;
                }
                const cv = cd.branch || cd.base;
                if (cv) {
                    td.textContent = cv.formula || (cv.value !== null && cv.value !== undefined ? String(cv.value) : "");
                }
                if (cd.kind === "removed") {
                    td.style.textDecoration = "line-through";
                    td.style.opacity = "0.6";
                }
            }
            tr.appendChild(td);
        }
        tbody.appendChild(tr);
    }
}

async function resolveRow(choice) {
    if (!state.selectedCell) return;
    const { sheet, row } = state.selectedCell;
    const sd = state.diff.sheets[sheet];
    if (!sd) return;
    const resolutions = [];
    const undoKeys = [];
    for (const [key, cd] of Object.entries(sd.cells)) {
        const [r, c] = key.split(",").map(Number);
        if (r === row && _needsResolution(cd.kind)) {
            resolutions.push({ sheet, row: r, col: c, choice });
            undoKeys.push(resolutionKey(sheet, r, c));
        }
    }
    if (resolutions.length === 0) {
        showToast("该行没有需要解决的单元格");
        return;
    }
    pushUndo(`第 ${row} 行 → ${choice}`, undoKeys);
    try {
        await fetch("/api/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ resolutions }),
        });
        for (const item of resolutions) {
            state.resolutions[resolutionKey(item.sheet, item.row, item.col)] = choice;
        }
        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`第 ${row} 行: 全部选择 ${choice} (${resolutions.length} 个)`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
    }
}

async function resolveCol(choice) {
    if (!state.selectedCell) return;
    const { sheet, col } = state.selectedCell;
    const sd = state.diff.sheets[sheet];
    if (!sd) return;
    const resolutions = [];
    const undoKeys = [];
    for (const [key, cd] of Object.entries(sd.cells)) {
        const [r, c] = key.split(",").map(Number);
        if (c === col && _needsResolution(cd.kind)) {
            resolutions.push({ sheet, row: r, col: c, choice });
            undoKeys.push(resolutionKey(sheet, r, c));
        }
    }
    if (resolutions.length === 0) {
        showToast("该列没有需要解决的单元格");
        return;
    }
    pushUndo(`列 ${colToLetter(col)} → ${choice}`, undoKeys);
    try {
        await fetch("/api/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ resolutions }),
        });
        for (const item of resolutions) {
            state.resolutions[resolutionKey(item.sheet, item.row, item.col)] = choice;
        }
        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`列 ${colToLetter(col)}: 全部选择 ${choice} (${resolutions.length} 个)`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
    }
}

async function deleteRow() {
    if (!state.selectedCell) return;
    const { sheet, row } = state.selectedCell;
    const sd = state.diff.sheets[sheet];
    if (!sd) return;
    if (!confirm(`确定删除第 ${row} 行？保存时将从输出中移除。`)) return;
    const resolutions = [];
    const undoKeys = [];
    for (const [key, cd] of Object.entries(sd.cells)) {
        const [r, c] = key.split(",").map(Number);
        if (r === row && cd.kind !== "unchanged") {
            resolutions.push({ sheet, row: r, col: c, choice: "delete" });
            undoKeys.push(resolutionKey(sheet, r, c));
        }
    }
    if (resolutions.length === 0) {
        showToast("该行没有变化的单元格");
        return;
    }
    pushUndo(`删除第 ${row} 行`, undoKeys);
    try {
        await fetch("/api/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ resolutions }),
        });
        for (const item of resolutions) {
            state.resolutions[resolutionKey(item.sheet, item.row, item.col)] = "delete";
        }
        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`第 ${row} 行已标记删除`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
    }
}

async function deleteCol() {
    if (!state.selectedCell) return;
    const { sheet, col } = state.selectedCell;
    const sd = state.diff.sheets[sheet];
    if (!sd) return;
    if (!confirm(`确定删除列 ${colToLetter(col)}？保存时将从输出中移除。`)) return;
    const resolutions = [];
    const undoKeys = [];
    for (const [key, cd] of Object.entries(sd.cells)) {
        const [r, c] = key.split(",").map(Number);
        if (c === col && cd.kind !== "unchanged") {
            resolutions.push({ sheet, row: r, col: c, choice: "delete" });
            undoKeys.push(resolutionKey(sheet, r, c));
        }
    }
    if (resolutions.length === 0) {
        showToast("该列没有变化的单元格");
        return;
    }
    pushUndo(`删除列 ${colToLetter(col)}`, undoKeys);
    try {
        await fetch("/api/resolve", {
            method: "POST",
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ resolutions }),
        });
        for (const item of resolutions) {
            state.resolutions[resolutionKey(item.sheet, item.row, item.col)] = "delete";
        }
        renderGrid(state.currentSheet);
        renderSheetTabs();
        updateStatus();
        showToast(`列 ${colToLetter(col)} 已标记删除`);
    } catch (e) {
        showToast(`Error: ${e.message}`);
    }
}

function jumpToNextConflict() {
    const sheetsToCheck = [state.currentSheet, ...state.diff.sheet_order.filter(n => n !== state.currentSheet)];
    for (const name of sheetsToCheck) {
        const sd = state.diff.sheets[name];
        if (!sd) continue;
        for (const [key, cd] of Object.entries(sd.cells)) {
            if (!_needsResolution(cd.kind)) continue;
            const realKey = `${name}:${key}`;
            if (state.resolutions[realKey]) continue;
            const [r, c] = key.split(",").map(Number);
            if (name !== state.currentSheet) {
                switchSheet(name);
            }
            setTimeout(() => {
                selectCell(name, r, c, cd);
                const cell = document.querySelector(`td[data-vrow="${r}"][data-vcol="${c}"]`);
                if (cell) cell.scrollIntoView({ behavior: "smooth", block: "center", inline: "center" });
            }, name !== state.currentSheet ? 100 : 0);
            return;
        }
    }
    showToast("所有冲突已解决！");
}

document.getElementById("btn-all-ours").onclick = () => bulkResolve("ours");
document.getElementById("btn-all-theirs").onclick = () => bulkResolve("theirs");
document.getElementById("btn-save").onclick = saveMerge;
document.getElementById("btn-cancel").onclick = cancelMerge;
document.getElementById("btn-undo").onclick = undo;

loadDiff();
