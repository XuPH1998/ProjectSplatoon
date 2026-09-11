from __future__ import annotations

import os
import socket
import sys
import threading
import webbrowser
from dataclasses import dataclass, field

from flask import Flask, current_app, jsonify, render_template, request

from excelcompare.core.merge import save_merged_with_styles, unresolved_count
from excelcompare.core.models import (
    CellResolution,
    DiffKind,
    ResolutionChoice,
    WorkbookData,
    WorkbookDiff,
)


@dataclass
class MergeState:
    diff: WorkbookDiff
    resolutions: dict[tuple[str, int, int], CellResolution]
    base: WorkbookData
    ours: WorkbookData
    theirs: WorkbookData
    output_path: str
    ours_path: str = ""
    theirs_path: str = ""
    is_git_driver: bool = False
    exit_code: int = 1
    keep_both_edits: dict = field(default_factory=dict)
    shutdown_event: threading.Event = field(default_factory=threading.Event)


def create_app(merge_state: MergeState) -> Flask:
    app = Flask(__name__,
                template_folder=str(__file__.replace("app.py", "templates")),
                static_folder=str(__file__.replace("app.py", "static")))
    app.config["MERGE_STATE"] = merge_state

    @app.route("/")
    def index():
        state: MergeState = current_app.config["MERGE_STATE"]
        return render_template("index.html",
                               conflict_count=state.diff.conflict_count,
                               output_path=state.output_path)

    @app.route("/api/diff")
    def get_diff():
        state: MergeState = current_app.config["MERGE_STATE"]
        data = state.diff.to_dict()
        resolved = {}
        for (sheet, row, col), res in state.resolutions.items():
            key = f"{sheet}:{row},{col}"
            if res.choice.value == "manual" and res.manual_value is not None:
                resolved[key] = {"choice": "manual", "value": res.manual_value}
            else:
                resolved[key] = res.choice.value
        data["resolutions"] = resolved
        return jsonify(data)

    @app.route("/api/resolve", methods=["POST"])
    def resolve():
        state: MergeState = current_app.config["MERGE_STATE"]
        body = request.get_json()
        for item in body.get("resolutions", []):
            sheet = item["sheet"]
            row = int(item["row"])
            col = int(item["col"])
            choice = ResolutionChoice(item["choice"])
            manual_value = item.get("manual_value")
            state.resolutions[(sheet, row, col)] = CellResolution(
                sheet=sheet, row=row, col=col,
                choice=choice, manual_value=manual_value,
            )
        remaining = unresolved_count(state.diff, state.resolutions)
        return jsonify({"ok": True, "unresolved": remaining})

    @app.route("/api/resolve/bulk", methods=["POST"])
    def resolve_bulk():
        state: MergeState = current_app.config["MERGE_STATE"]
        body = request.get_json()
        sheet_filter = body.get("sheet")
        choice = ResolutionChoice(body["choice"])

        for sheet_name, sd in state.diff.sheets.items():
            if sheet_filter and sheet_name != sheet_filter:
                continue
            for (row, col), cd in sd.cells.items():
                if cd.kind not in (DiffKind.UNCHANGED,):
                    if cd.kind == DiffKind.BOTH_SAME:
                        continue
                    state.resolutions[(sheet_name, row, col)] = CellResolution(
                        sheet=sheet_name, row=row, col=col, choice=choice,
                    )
        remaining = unresolved_count(state.diff, state.resolutions)
        return jsonify({"ok": True, "unresolved": remaining})

    @app.route("/api/unresolve", methods=["POST"])
    def unresolve():
        state: MergeState = current_app.config["MERGE_STATE"]
        body = request.get_json()
        for item in body.get("items", []):
            sheet = item["sheet"]
            row = int(item["row"])
            col = int(item["col"])
            state.resolutions.pop((sheet, row, col), None)
        remaining = unresolved_count(state.diff, state.resolutions)
        return jsonify({"ok": True, "unresolved": remaining})

    @app.route("/api/save", methods=["POST"])
    def save():
        state: MergeState = current_app.config["MERGE_STATE"]
        body = request.get_json(silent=True) or {}
        state.keep_both_edits = body.get("keep_both_edits", {})
        remaining = unresolved_count(state.diff, state.resolutions)
        save_merged_with_styles(
            state.diff, state.resolutions,
            state.ours_path, state.theirs_path,
            state.output_path, state.theirs,
            keep_both_edits=state.keep_both_edits,
        )
        state.exit_code = 0 if remaining == 0 else 1
        return jsonify({"ok": True, "unresolved": remaining, "output": state.output_path})

    @app.route("/api/cancel", methods=["POST"])
    def cancel():
        state: MergeState = current_app.config["MERGE_STATE"]
        state.exit_code = 1
        return jsonify({"ok": True, "cancelled": True})

    @app.route("/api/exit", methods=["POST"])
    def exit_server():
        state: MergeState = current_app.config["MERGE_STATE"]
        state.shutdown_event.set()
        return jsonify({"ok": True})

    @app.route("/api/status")
    def status():
        state: MergeState = current_app.config["MERGE_STATE"]
        remaining = unresolved_count(state.diff, state.resolutions)
        total_conflicts = state.diff.conflict_count
        return jsonify({
            "total_conflicts": total_conflicts,
            "resolved": total_conflicts - remaining,
            "unresolved": remaining,
        })

    @app.route("/api/branch-diff")
    def branch_diff():
        state: MergeState = current_app.config["MERGE_STATE"]
        sheet_name = request.args.get("sheet")
        if not sheet_name:
            return jsonify({"error": "sheet required"}), 400

        from excelcompare.core.diff import diff_two_way, _try_col_align

        base_sheet = state.base.sheets.get(sheet_name)
        ours_sheet = state.ours.sheets.get(sheet_name)
        theirs_sheet = state.theirs.sheets.get(sheet_name)

        sd = state.diff.sheets.get(sheet_name)
        col_mapping = sd.col_mapping if sd else None
        key_col = sd.key_col if sd else None
        header_count = sd.header_count if sd else 0

        ours_diff = diff_two_way(base_sheet, ours_sheet, col_mapping, branch_key="ours", key_col=key_col, header_count=header_count)
        theirs_diff = diff_two_way(base_sheet, theirs_sheet, col_mapping, branch_key="theirs", key_col=key_col, header_count=header_count)

        return jsonify({
            "ours": ours_diff,
            "theirs": theirs_diff,
            "col_mapping": col_mapping,
            "key_col": key_col,
        })

    @app.route("/api/rediff", methods=["POST"])
    def rediff():
        state: MergeState = current_app.config["MERGE_STATE"]
        body = request.get_json()
        sheet_name = body["sheet"]
        key_col = body.get("key_col")

        from excelcompare.core.diff import detect_key_col, diff_sheets, diff_sheets_aligned, _try_col_align

        base_sheet = state.base.sheets.get(sheet_name)
        ours_sheet = state.ours.sheets.get(sheet_name)
        theirs_sheet = state.theirs.sheets.get(sheet_name)

        warning = None
        if key_col is not None and base_sheet and ours_sheet and theirs_sheet:
            proj_base, proj_ours, proj_theirs, col_mapping = _try_col_align(base_sheet, ours_sheet, theirs_sheet)
            col_aligned = col_mapping is not None
            work_base = proj_base if col_aligned else base_sheet
            work_ours = proj_ours if col_aligned else ours_sheet
            work_theirs = proj_theirs if col_aligned else theirs_sheet
            from excelcompare.core.diff import _detect_header_rows
            hc = _detect_header_rows(work_base, work_ours, work_theirs)
            new_sd = diff_sheets_aligned(sheet_name, work_base, work_ours, work_theirs, int(key_col), hc)
            new_sd.col_aligned = col_aligned
            new_sd.col_mapping = col_mapping
        else:
            new_sd = diff_sheets(sheet_name, base_sheet, ours_sheet, theirs_sheet)

        old_cc = state.diff.sheets[sheet_name].conflict_count if sheet_name in state.diff.sheets else 0
        state.diff.sheets[sheet_name] = new_sd
        state.diff.conflict_count = state.diff.conflict_count - old_cc + new_sd.conflict_count

        keys_to_remove = [k for k in state.resolutions if k[0] == sheet_name]
        for k in keys_to_remove:
            del state.resolutions[k]

        from excelcompare.core.merge import auto_merge as _auto_merge
        auto_res = _auto_merge(state.diff)
        for k, v in auto_res.items():
            if k[0] == sheet_name:
                state.resolutions[k] = v

        resp = {"ok": True, "sheet": new_sd.to_dict()}
        if warning:
            resp["warning"] = warning
        return jsonify(resp)

    return app


def _find_free_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def run_merge_server(
    diff: WorkbookDiff,
    resolutions: dict[tuple[str, int, int], CellResolution],
    base: WorkbookData,
    ours: WorkbookData,
    theirs: WorkbookData,
    output_path: str,
    is_git_driver: bool = False,
    ours_path: str = "",
    theirs_path: str = "",
) -> int:
    state = MergeState(
        diff=diff,
        resolutions=resolutions,
        base=base,
        ours=ours,
        theirs=theirs,
        output_path=output_path,
        ours_path=ours_path,
        theirs_path=theirs_path,
        is_git_driver=is_git_driver,
    )

    port = _find_free_port()
    app = create_app(state)

    server_thread = threading.Thread(
        target=lambda: app.run(host="127.0.0.1", port=port, debug=False, use_reloader=False),
        daemon=True,
    )
    server_thread.start()

    url = f"http://127.0.0.1:{port}"
    print(f"ExcelCompare: Opening merge UI at {url}", file=sys.stderr)
    webbrowser.open(url)

    state.shutdown_event.wait()
    threading.Timer(0.5, lambda: os._exit(state.exit_code)).start()
    import time
    time.sleep(1)
