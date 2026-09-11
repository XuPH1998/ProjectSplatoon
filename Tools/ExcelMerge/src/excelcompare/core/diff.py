from __future__ import annotations

from difflib import SequenceMatcher
from typing import Any

from excelcompare.core.models import (
    CellDiff,
    CellValue,
    DiffKind,
    SheetData,
    SheetDiff,
    SheetDiffKind,
    WorkbookData,
    WorkbookDiff,
)


def diff_cells(
    base: CellValue | None,
    ours: CellValue | None,
    theirs: CellValue | None,
) -> DiffKind:
    base_empty = base is None or base.is_empty()
    ours_empty = ours is None or ours.is_empty()
    theirs_empty = theirs is None or theirs.is_empty()

    if base_empty:
        base = None
    if ours_empty:
        ours = None
    if theirs_empty:
        theirs = None

    base_cv = base or CellValue()
    ours_cv = ours or CellValue()
    theirs_cv = theirs or CellValue()

    ours_changed = not ours_cv.equals(base_cv)
    theirs_changed = not theirs_cv.equals(base_cv)

    if not ours_changed and not theirs_changed:
        return DiffKind.UNCHANGED
    if ours_changed and not theirs_changed:
        return DiffKind.OURS_ONLY
    if not ours_changed and theirs_changed:
        return DiffKind.THEIRS_ONLY
    if ours_cv.equals(theirs_cv):
        return DiffKind.BOTH_SAME
    return DiffKind.CONFLICT


def _normalize_key(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, float) and value == int(value):
        return str(int(value))
    return str(value)


def _data_rows(sheet: SheetData) -> list[int]:
    rows: set[int] = set()
    for (row, _), cv in sheet.cells.items():
        if cv and not cv.is_empty():
            rows.add(row)
    return sorted(rows)


def _detect_header_rows(*sheets: SheetData | None) -> int:
    """Detect header rows where column 1 starts with '#'. Returns last header row number (0 if none)."""
    header_count = 0
    for sheet in sheets:
        if sheet is None:
            continue
        for row in range(1, sheet.max_row + 1):
            cv = sheet.cells.get((row, 1))
            if cv is None or cv.is_empty():
                break
            val = str(cv.value) if cv.value is not None else ""
            if val.startswith("#"):
                header_count = max(header_count, row)
            else:
                break
    return header_count


def detect_key_col(
    base: SheetData,
    ours: SheetData | None = None,
    theirs: SheetData | None = None,
) -> tuple[int | None, int]:
    """Returns (key_col, header_count)."""
    header_count = _detect_header_rows(base, ours, theirs)

    if not base or base.max_col == 0 or base.max_row == 0:
        return None, header_count

    base_rows = [r for r in _data_rows(base) if r > header_count]
    if not base_rows:
        return None, header_count

    preferred_names = {"id", "key"}
    preferred_cols: list[int] = []
    if header_count:
        for sheet in (base, ours, theirs):
            if sheet is None:
                continue
            for row in range(1, header_count + 1):
                for col in range(1, sheet.max_col + 1):
                    cv = sheet.cells.get((row, col))
                    if cv is None or cv.is_empty():
                        continue
                    value = cv.formula if cv.formula else cv.value
                    if isinstance(value, str) and value.strip().lower() in preferred_names:
                        if col not in preferred_cols:
                            preferred_cols.append(col)

    cols_to_try = preferred_cols[:]
    if base.max_col >= 2 and 2 not in cols_to_try:
        cols_to_try.append(2)
    cols_to_try.extend(c for c in range(1, base.max_col + 1) if c not in cols_to_try)
    for col in cols_to_try:
        values: list[str] = []
        all_filled = True
        for row in base_rows:
            cv = base.cells.get((row, col))
            if cv is None or cv.is_empty():
                all_filled = False
                break
            values.append(_normalize_key(cv.value if not cv.formula else cv.formula))

        if not all_filled or not values:
            continue

        if len(values) != len(set(values)):
            continue

        valid = True
        for sheet in (ours, theirs):
            if sheet is None:
                continue
            sheet_rows = [r for r in _data_rows(sheet) if r > header_count]
            seen: set[str] = set()
            for row in sheet_rows:
                cv = sheet.cells.get((row, col))
                if cv is None or cv.is_empty():
                    valid = False
                    break
                k = _normalize_key(cv.value if not cv.formula else cv.formula)
                if k in seen:
                    valid = False
                    break
                seen.add(k)
            if not valid:
                break

        if valid:
            base_set = set(values)
            overlap_ok = True
            for sheet in (ours, theirs):
                if sheet is None:
                    continue
                sheet_keys: set[str] = set()
                for row in _data_rows(sheet):
                    if row <= header_count:
                        continue
                    cv = sheet.cells.get((row, col))
                    if cv and not cv.is_empty():
                        sheet_keys.add(_normalize_key(cv.value if not cv.formula else cv.formula))
                overlap = len(base_set & sheet_keys)
                if len(base_set) > 0 and overlap / len(base_set) < 0.5:
                    overlap_ok = False
                    break
            if overlap_ok:
                return col, header_count

    return None, header_count


def build_row_index(sheet: SheetData, key_col: int, header_count: int = 0) -> dict[str, int]:
    index: dict[str, int] = {}
    for row in range(1, sheet.max_row + 1):
        if row <= header_count:
            index[f"__h{row}__"] = row
            continue
        cv = sheet.cells.get((row, key_col))
        if cv is None or cv.is_empty():
            continue
        k = _normalize_key(cv.value if not cv.formula else cv.formula)
        index[k] = row
    return index


def _build_unified_order(
    base_idx: dict[str, int],
    ours_idx: dict[str, int],
    theirs_idx: dict[str, int],
) -> list[str]:
    base_keys = sorted(base_idx.keys(), key=lambda k: base_idx[k])
    ours_keys = sorted(ours_idx.keys(), key=lambda k: ours_idx[k])
    theirs_keys = sorted(theirs_idx.keys(), key=lambda k: theirs_idx[k])

    base_set = set(base_keys)
    ours_new = [k for k in ours_keys if k not in base_set]
    theirs_new = [k for k in theirs_keys if k not in base_set]

    ours_pos_after: dict[str, list[str]] = {}
    prev_base_key: str | None = None
    for k in ours_keys:
        if k in base_set:
            prev_base_key = k
        else:
            anchor = prev_base_key or "__START__"
            ours_pos_after.setdefault(anchor, []).append(k)

    theirs_pos_after: dict[str, list[str]] = {}
    prev_base_key = None
    for k in theirs_keys:
        if k in base_set:
            prev_base_key = k
        else:
            anchor = prev_base_key or "__START__"
            theirs_pos_after.setdefault(anchor, []).append(k)

    result: list[str] = []
    seen: set[str] = set()

    for keys in (ours_pos_after.get("__START__", []),
                 theirs_pos_after.get("__START__", [])):
        for k in keys:
            if k not in seen:
                result.append(k)
                seen.add(k)

    for base_key in base_keys:
        if base_key not in seen:
            result.append(base_key)
            seen.add(base_key)

        for keys in (ours_pos_after.get(base_key, []),
                     theirs_pos_after.get(base_key, [])):
            for k in keys:
                if k not in seen:
                    result.append(k)
                    seen.add(k)

    for k in ours_new + theirs_new:
        if k not in seen:
            result.append(k)
            seen.add(k)

    return result


def build_col_index(sheet: SheetData | None, header_row: int = 1) -> dict[str, int]:
    if sheet is None:
        return {}
    index: dict[str, int] = {}
    for col in range(1, sheet.max_col + 1):
        cv = sheet.cells.get((header_row, col))
        if cv is None or cv.is_empty():
            continue
        if cv.data_type not in ("s", "f") and not isinstance(cv.value, str):
            continue
        k = _normalize_key(cv.value if not cv.formula else cv.formula)
        if k and k not in index:
            index[k] = col
    return index


def project_sheet_to_virtual_cols(
    sheet: SheetData,
    phys_to_virtual: dict[int, int],
) -> SheetData:
    projected = SheetData(name=sheet.name)
    projected.max_row = sheet.max_row
    projected.max_col = max(phys_to_virtual.values()) if phys_to_virtual else 0
    for (row, col), cv in sheet.cells.items():
        vcol = phys_to_virtual.get(col)
        if vcol is not None:
            projected.cells[(row, vcol)] = cv
    return projected


def build_unified_row_order(
    base_idx: dict[str, int],
    ours_idx: dict[str, int],
    theirs_idx: dict[str, int],
) -> list[str]:
    return _build_unified_order(base_idx, ours_idx, theirs_idx)


def _row_signature(sheet: SheetData, row: int, max_col: int) -> tuple:
    vals = []
    for col in range(1, max_col + 1):
        cv = sheet.cells.get((row, col))
        if cv is None or cv.is_empty():
            vals.append(None)
        elif cv.formula:
            vals.append(cv.formula)
        else:
            vals.append(cv.value)
    return tuple(vals)


def _match_rows(
    base_sigs: list[tuple],
    branch_sigs: list[tuple],
    base_rows: list[int],
    branch_rows: list[int],
) -> tuple[list[tuple[int, int]], list[int], list[int]]:
    sm = SequenceMatcher(None, base_sigs, branch_sigs, autojunk=False)
    matched: list[tuple[int, int]] = []
    base_only: list[int] = []
    branch_only: list[int] = []

    for tag, i1, i2, j1, j2 in sm.get_opcodes():
        if tag == "equal":
            for i, j in zip(range(i1, i2), range(j1, j2)):
                matched.append((base_rows[i], branch_rows[j]))
        elif tag == "replace":
            bi = list(range(i1, i2))
            bj = list(range(j1, j2))
            pairs = min(len(bi), len(bj))
            for k in range(pairs):
                matched.append((base_rows[bi[k]], branch_rows[bj[k]]))
            for k in range(pairs, len(bi)):
                base_only.append(base_rows[bi[k]])
            for k in range(pairs, len(bj)):
                branch_only.append(branch_rows[bj[k]])
        elif tag == "delete":
            for i in range(i1, i2):
                base_only.append(base_rows[i])
        elif tag == "insert":
            for j in range(j1, j2):
                branch_only.append(branch_rows[j])

    return matched, base_only, branch_only


def _fill_row_context(
    cells: dict[tuple[int, int], CellDiff],
    max_row: int,
    max_col: int,
    base: SheetData | None,
    ours: SheetData | None,
    theirs: SheetData | None,
    vrow_to_phys: dict[int, tuple[int | None, int | None, int | None]],
) -> None:
    for vrow in range(1, max_row + 1):
        phys = vrow_to_phys.get(vrow, (vrow, vrow, vrow))
        base_row, ours_row, theirs_row = phys
        for col in range(1, max_col + 1):
            if (vrow, col) in cells:
                continue
            base_cv = base.cells.get((base_row, col)) if base and base_row else None
            ours_cv = ours.cells.get((ours_row, col)) if ours and ours_row else None
            theirs_cv = theirs.cells.get((theirs_row, col)) if theirs and theirs_row else None
            cv = ours_cv or theirs_cv or base_cv
            if cv and not cv.is_empty():
                cells[(vrow, col)] = CellDiff(
                    row=vrow, col=col,
                    kind=DiffKind.UNCHANGED,
                    base=base_cv, ours=ours_cv, theirs=theirs_cv,
                )


def diff_sheets_aligned(
    sheet_name: str,
    base: SheetData,
    ours: SheetData,
    theirs: SheetData,
    key_col: int,
    header_count: int = 0,
) -> SheetDiff:
    base_idx = build_row_index(base, key_col, header_count)
    ours_idx = build_row_index(ours, key_col, header_count)
    theirs_idx = build_row_index(theirs, key_col, header_count)

    row_order = build_unified_row_order(base_idx, ours_idx, theirs_idx)
    max_col = max(
        (s.max_col for s in (base, ours, theirs) if s and s.max_col),
        default=0,
    )

    cells: dict[tuple[int, int], CellDiff] = {}
    conflict_count = 0
    row_mapping: list[dict] = []

    for virtual_row, key in enumerate(row_order, start=1):
        base_row = base_idx.get(key)
        ours_row = ours_idx.get(key)
        theirs_row = theirs_idx.get(key)

        row_mapping.append({
            "key": key,
            "virtual_row": virtual_row,
            "base_row": base_row,
            "ours_row": ours_row,
            "theirs_row": theirs_row,
        })

        for col in range(1, max_col + 1):
            base_cv = base.cells.get((base_row, col)) if base_row else None
            ours_cv = ours.cells.get((ours_row, col)) if ours_row else None
            theirs_cv = theirs.cells.get((theirs_row, col)) if theirs_row else None

            kind = diff_cells(base_cv, ours_cv, theirs_cv)
            if kind == DiffKind.UNCHANGED:
                continue

            if kind != DiffKind.BOTH_SAME:
                conflict_count += 1

            cells[(virtual_row, col)] = CellDiff(
                row=virtual_row,
                col=col,
                kind=kind,
                base=base_cv,
                ours=ours_cv,
                theirs=theirs_cv,
                orig_row_base=base_row,
                orig_row_ours=ours_row,
                orig_row_theirs=theirs_row,
            )

    max_row = len(row_order)
    vrow_phys = {rm["virtual_row"]: (rm["base_row"], rm["ours_row"], rm["theirs_row"]) for rm in row_mapping}
    _fill_row_context(cells, max_row, max_col, base, ours, theirs, vrow_phys)
    sheet_kind = SheetDiffKind.MODIFIED if cells else SheetDiffKind.UNCHANGED

    return SheetDiff(
        sheet_name=sheet_name,
        kind=sheet_kind,
        cells=cells,
        max_row=max_row,
        max_col=max_col,
        conflict_count=conflict_count,
        key_col=key_col,
        aligned=True,
        row_mapping=row_mapping,
        header_count=header_count,
    )


def diff_sheets_lcs(
    sheet_name: str,
    base: SheetData,
    ours: SheetData,
    theirs: SheetData,
) -> SheetDiff:
    max_col = max(
        (s.max_col for s in (base, ours, theirs) if s and s.max_col),
        default=0,
    )

    base_rows = _data_rows(base)
    ours_rows = _data_rows(ours)
    theirs_rows = _data_rows(theirs)

    base_sigs = [_row_signature(base, r, max_col) for r in base_rows]
    ours_sigs = [_row_signature(ours, r, max_col) for r in ours_rows]
    theirs_sigs = [_row_signature(theirs, r, max_col) for r in theirs_rows]

    bo_matched, bo_base_only, bo_ours_only = _match_rows(base_sigs, ours_sigs, base_rows, ours_rows)
    bt_matched, bt_base_only, bt_theirs_only = _match_rows(base_sigs, theirs_sigs, base_rows, theirs_rows)

    base_to_ours: dict[int, int] = {b: o for b, o in bo_matched}
    base_to_theirs: dict[int, int] = {b: t for b, t in bt_matched}
    ours_to_base: dict[int, int] = {o: b for b, o in bo_matched}
    theirs_to_base: dict[int, int] = {t: b for b, t in bt_matched}

    base_idx: dict[str, int] = {}
    ours_idx: dict[str, int] = {}
    theirs_idx: dict[str, int] = {}

    for i, br in enumerate(base_rows):
        key = f"B{i}"
        base_idx[key] = br
        if br in base_to_ours:
            ours_idx[key] = base_to_ours[br]
        if br in base_to_theirs:
            theirs_idx[key] = base_to_theirs[br]

    oi = 0
    for orow in bo_ours_only:
        key = f"O{oi}"
        ours_idx[key] = orow
        oi += 1

    ti = 0
    for trow in bt_theirs_only:
        key = f"T{ti}"
        theirs_idx[key] = trow
        ti += 1

    row_order = _build_unified_order(base_idx, ours_idx, theirs_idx)

    cells: dict[tuple[int, int], CellDiff] = {}
    conflict_count = 0
    row_mapping: list[dict] = []

    for virtual_row, key in enumerate(row_order, start=1):
        base_row = base_idx.get(key)
        ours_row = ours_idx.get(key)
        theirs_row = theirs_idx.get(key)

        row_mapping.append({
            "key": key,
            "virtual_row": virtual_row,
            "base_row": base_row,
            "ours_row": ours_row,
            "theirs_row": theirs_row,
        })

        for col in range(1, max_col + 1):
            base_cv = base.cells.get((base_row, col)) if base_row else None
            ours_cv = ours.cells.get((ours_row, col)) if ours_row else None
            theirs_cv = theirs.cells.get((theirs_row, col)) if theirs_row else None

            kind = diff_cells(base_cv, ours_cv, theirs_cv)
            if kind == DiffKind.UNCHANGED:
                continue

            if kind != DiffKind.BOTH_SAME:
                conflict_count += 1

            cells[(virtual_row, col)] = CellDiff(
                row=virtual_row,
                col=col,
                kind=kind,
                base=base_cv,
                ours=ours_cv,
                theirs=theirs_cv,
                orig_row_base=base_row,
                orig_row_ours=ours_row,
                orig_row_theirs=theirs_row,
            )

    max_row = len(row_order)
    vrow_phys = {rm["virtual_row"]: (rm["base_row"], rm["ours_row"], rm["theirs_row"]) for rm in row_mapping}
    _fill_row_context(cells, max_row, max_col, base, ours, theirs, vrow_phys)
    sheet_kind = SheetDiffKind.MODIFIED if cells else SheetDiffKind.UNCHANGED

    return SheetDiff(
        sheet_name=sheet_name,
        kind=sheet_kind,
        cells=cells,
        max_row=max_row,
        max_col=max_col,
        conflict_count=conflict_count,
        key_col=None,
        aligned=True,
        row_mapping=row_mapping,
    )


def _try_col_align(
    base: SheetData | None,
    ours: SheetData | None,
    theirs: SheetData | None,
) -> tuple[SheetData | None, SheetData | None, SheetData | None, list[dict] | None]:
    if not (base and ours and theirs):
        return base, ours, theirs, None

    base_ci = build_col_index(base)
    ours_ci = build_col_index(ours)
    theirs_ci = build_col_index(theirs)

    all_headers = set(base_ci) | set(ours_ci) | set(theirs_ci)
    if not all_headers:
        return base, ours, theirs, None

    if base_ci == ours_ci == theirs_ci:
        return base, ours, theirs, None

    base_set = set(base_ci)
    ours_set = set(ours_ci)
    theirs_set = set(theirs_ci)
    if not base_set:
        return base, ours, theirs, None
    ours_overlap = len(base_set & ours_set) / len(base_set) if base_set else 0
    theirs_overlap = len(base_set & theirs_set) / len(base_set) if base_set else 0
    if ours_overlap < 0.5 or theirs_overlap < 0.5:
        return base, ours, theirs, None

    col_order = _build_unified_order(base_ci, ours_ci, theirs_ci)

    col_mapping: list[dict] = []
    base_p2v: dict[int, int] = {}
    ours_p2v: dict[int, int] = {}
    theirs_p2v: dict[int, int] = {}

    for vcol, header in enumerate(col_order, start=1):
        bc = base_ci.get(header)
        oc = ours_ci.get(header)
        tc = theirs_ci.get(header)
        col_mapping.append({
            "header": header,
            "virtual_col": vcol,
            "base_col": bc,
            "ours_col": oc,
            "theirs_col": tc,
        })
        if bc is not None:
            base_p2v[bc] = vcol
        if oc is not None:
            ours_p2v[oc] = vcol
        if tc is not None:
            theirs_p2v[tc] = vcol

    proj_base = project_sheet_to_virtual_cols(base, base_p2v)
    proj_ours = project_sheet_to_virtual_cols(ours, ours_p2v)
    proj_theirs = project_sheet_to_virtual_cols(theirs, theirs_p2v)

    return proj_base, proj_ours, proj_theirs, col_mapping


def diff_sheets(
    sheet_name: str,
    base: SheetData | None,
    ours: SheetData | None,
    theirs: SheetData | None,
) -> SheetDiff:
    if base is None and ours is not None and theirs is None:
        return SheetDiff(
            sheet_name=sheet_name,
            kind=SheetDiffKind.ADDED_OURS,
            cells={},
            max_row=ours.max_row,
            max_col=ours.max_col,
        )
    if base is None and ours is None and theirs is not None:
        return SheetDiff(
            sheet_name=sheet_name,
            kind=SheetDiffKind.ADDED_THEIRS,
            cells={},
            max_row=theirs.max_row,
            max_col=theirs.max_col,
        )

    proj_base, proj_ours, proj_theirs, col_mapping = _try_col_align(base, ours, theirs)
    col_aligned = col_mapping is not None
    work_base = proj_base if col_aligned else base
    work_ours = proj_ours if col_aligned else ours
    work_theirs = proj_theirs if col_aligned else theirs

    if work_base is not None and work_ours is not None and work_theirs is not None:
        key_col, header_count = detect_key_col(work_base, work_ours, work_theirs)
        if key_col is not None:
            sd = diff_sheets_aligned(sheet_name, work_base, work_ours, work_theirs, key_col, header_count)
            sd.col_aligned = col_aligned
            sd.col_mapping = col_mapping
            return sd

        sd = diff_sheets_lcs(sheet_name, work_base, work_ours, work_theirs)
        sd.col_aligned = col_aligned
        sd.col_mapping = col_mapping
        return sd

    all_keys: set[tuple[int, int]] = set()
    for s in (work_base, work_ours, work_theirs):
        if s:
            all_keys.update(s.cells.keys())

    cells: dict[tuple[int, int], CellDiff] = {}
    conflict_count = 0

    for key in all_keys:
        base_cv = work_base.cells.get(key) if work_base else None
        ours_cv = work_ours.cells.get(key) if work_ours else None
        theirs_cv = work_theirs.cells.get(key) if work_theirs else None

        kind = diff_cells(base_cv, ours_cv, theirs_cv)
        if kind == DiffKind.UNCHANGED:
            continue

        if kind == DiffKind.CONFLICT:
            conflict_count += 1

        cells[key] = CellDiff(
            row=key[0],
            col=key[1],
            kind=kind,
            base=base_cv,
            ours=ours_cv,
            theirs=theirs_cv,
        )

    max_row = max((s.max_row for s in (work_base, work_ours, work_theirs) if s), default=0)
    max_col = max((s.max_col for s in (work_base, work_ours, work_theirs) if s), default=0)

    vrow_phys = {r: (r, r, r) for r in range(1, max_row + 1)}
    _fill_row_context(cells, max_row, max_col, work_base, work_ours, work_theirs, vrow_phys)

    sheet_kind = SheetDiffKind.MODIFIED if cells else SheetDiffKind.UNCHANGED
    return SheetDiff(
        sheet_name=sheet_name,
        kind=sheet_kind,
        cells=cells,
        max_row=max_row,
        max_col=max_col,
        conflict_count=conflict_count,
        col_aligned=col_aligned,
        col_mapping=col_mapping,
    )


def _diff_two_way_cells(
    work_base: SheetData | None,
    work_branch: SheetData | None,
    base_row: int | None,
    branch_row: int | None,
    virtual_row: int,
    max_col: int,
    cells: dict[str, dict],
) -> None:
    for col in range(1, max_col + 1):
        base_cv = work_base.cells.get((base_row, col)) if work_base and base_row else None
        branch_cv = work_branch.cells.get((branch_row, col)) if work_branch and branch_row else None

        base_empty = base_cv is None or base_cv.is_empty()
        branch_empty = branch_cv is None or branch_cv.is_empty()

        if base_empty and branch_empty:
            continue
        if base_empty and not branch_empty:
            kind = "added"
        elif not base_empty and branch_empty:
            kind = "removed"
        elif base_cv.equals(branch_cv):
            continue
        else:
            kind = "changed"

        cells[f"{virtual_row},{col}"] = {
            "kind": kind,
            "base": base_cv.to_dict() if base_cv and not base_empty else None,
            "branch": branch_cv.to_dict() if branch_cv and not branch_empty else None,
        }


def diff_two_way(
    base: SheetData | None,
    branch: SheetData | None,
    col_mapping: list[dict] | None = None,
    branch_key: str = "ours",
    key_col: int | None = None,
    header_count: int = 0,
) -> dict:
    if not base and not branch:
        return {"cells": {}, "max_row": 0, "max_col": 0}

    work_base = base
    work_branch = branch

    if col_mapping and base and branch:
        base_p2v: dict[int, int] = {}
        branch_p2v: dict[int, int] = {}
        col_key = f"{branch_key}_col"
        for cm in col_mapping:
            if cm.get("base_col") is not None:
                base_p2v[cm["base_col"]] = cm["virtual_col"]
            if cm.get(col_key) is not None:
                branch_p2v[cm[col_key]] = cm["virtual_col"]
        work_base = project_sheet_to_virtual_cols(base, base_p2v) if base_p2v else base
        work_branch = project_sheet_to_virtual_cols(branch, branch_p2v) if branch_p2v else branch

    max_col = max((s.max_col for s in (work_base, work_branch) if s), default=0)
    cells: dict[str, dict] = {}

    vrow_to_phys: dict[int, tuple[int | None, int | None]] = {}

    if work_base and work_branch and key_col:
        base_idx = build_row_index(work_base, key_col, header_count)
        branch_idx = build_row_index(work_branch, key_col, header_count)
        unified = _build_unified_order(base_idx, branch_idx, {})
        for vrow, k in enumerate(unified, start=1):
            br = base_idx.get(k)
            bhr = branch_idx.get(k)
            vrow_to_phys[vrow] = (br, bhr)
            _diff_two_way_cells(work_base, work_branch, br, bhr, vrow, max_col, cells)
        max_row = len(unified)
    elif work_base and work_branch:
        base_rows = _data_rows(work_base)
        branch_rows = _data_rows(work_branch)
        base_sigs = [_row_signature(work_base, r, max_col) for r in base_rows]
        branch_sigs = [_row_signature(work_branch, r, max_col) for r in branch_rows]
        matched, base_only, branch_only = _match_rows(base_sigs, branch_sigs, base_rows, branch_rows)

        base_to_branch = {b: br for b, br in matched}

        base_idx_lcs: dict[str, int] = {}
        branch_idx_lcs: dict[str, int] = {}
        for i, br in enumerate(base_rows):
            key = f"B{i}"
            base_idx_lcs[key] = br
            if br in base_to_branch:
                branch_idx_lcs[key] = base_to_branch[br]
        for j, br_row in enumerate(branch_only):
            key = f"N{j}"
            branch_idx_lcs[key] = br_row

        unified = _build_unified_order(base_idx_lcs, branch_idx_lcs, {})
        for vrow, k in enumerate(unified, start=1):
            br = base_idx_lcs.get(k)
            bhr = branch_idx_lcs.get(k)
            vrow_to_phys[vrow] = (br, bhr)
            _diff_two_way_cells(work_base, work_branch, br, bhr, vrow, max_col, cells)
        max_row = len(unified)
    else:
        max_row = max((s.max_row for s in (work_base, work_branch) if s), default=0)
        for row in range(1, max_row + 1):
            vrow_to_phys[row] = (row, row)
            _diff_two_way_cells(work_base, work_branch, row, row, row, max_col, cells)

    for vrow in range(1, max_row + 1):
        phys = vrow_to_phys.get(vrow, (vrow, vrow))
        base_r, branch_r = phys
        for col in range(1, max_col + 1):
            ck = f"{vrow},{col}"
            if ck in cells:
                continue
            base_cv = work_base.cells.get((base_r, col)) if work_base and base_r else None
            branch_cv = work_branch.cells.get((branch_r, col)) if work_branch and branch_r else None
            cv = branch_cv or base_cv
            if cv and not cv.is_empty():
                cells[ck] = {
                    "kind": "unchanged",
                    "base": base_cv.to_dict() if base_cv and not base_cv.is_empty() else (cv.to_dict() if cv else None),
                    "branch": branch_cv.to_dict() if branch_cv and not branch_cv.is_empty() else (cv.to_dict() if cv else None),
                }

    return {"cells": cells, "max_row": max_row, "max_col": max_col}


def diff_workbooks(
    base: WorkbookData,
    ours: WorkbookData,
    theirs: WorkbookData,
) -> WorkbookDiff:
    all_sheet_names: list[str] = []
    seen: set[str] = set()
    for name in ours.sheet_order + theirs.sheet_order + base.sheet_order:
        if name not in seen:
            all_sheet_names.append(name)
            seen.add(name)

    result = WorkbookDiff()
    result.sheet_order = all_sheet_names

    for name in all_sheet_names:
        base_sheet = base.sheets.get(name)
        ours_sheet = ours.sheets.get(name)
        theirs_sheet = theirs.sheets.get(name)

        if not ours_sheet and not theirs_sheet:
            continue

        sheet_diff = diff_sheets(name, base_sheet, ours_sheet, theirs_sheet)
        result.sheets[name] = sheet_diff
        result.conflict_count += sheet_diff.conflict_count

        for cd in sheet_diff.cells.values():
            if cd.kind == DiffKind.BOTH_SAME:
                result.auto_resolved_count += 1

    return result
