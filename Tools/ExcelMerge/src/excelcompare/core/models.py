from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Any


@dataclass
class CellValue:
    value: Any = None
    formula: str | None = None
    data_type: str = "n"

    def equals(self, other: CellValue | None) -> bool:
        if other is None:
            return self.value is None and self.formula is None
        if self.formula and other.formula:
            return self.formula == other.formula
        if self.formula or other.formula:
            return False
        return self.value == other.value and self.data_type == other.data_type

    def is_empty(self) -> bool:
        return self.value is None and self.formula is None

    def display(self) -> str:
        if self.formula:
            return self.formula
        if self.value is None:
            return ""
        return str(self.value)

    def to_dict(self) -> dict:
        return {
            "value": self.value if not isinstance(self.value, bytes) else str(self.value),
            "formula": self.formula,
            "data_type": self.data_type,
        }


@dataclass
class SheetData:
    name: str
    cells: dict[tuple[int, int], CellValue] = field(default_factory=dict)
    max_row: int = 0
    max_col: int = 0


@dataclass
class WorkbookData:
    sheets: dict[str, SheetData] = field(default_factory=dict)
    sheet_order: list[str] = field(default_factory=list)


class DiffKind(str, Enum):
    UNCHANGED = "unchanged"
    OURS_ONLY = "ours"
    THEIRS_ONLY = "theirs"
    CONFLICT = "conflict"
    BOTH_SAME = "both_same"


@dataclass
class CellDiff:
    row: int
    col: int
    kind: DiffKind
    base: CellValue | None = None
    ours: CellValue | None = None
    theirs: CellValue | None = None
    orig_row_base: int | None = None
    orig_row_ours: int | None = None
    orig_row_theirs: int | None = None

    def to_dict(self) -> dict:
        d = {
            "kind": self.kind.value,
            "base": self.base.to_dict() if self.base else None,
            "ours": self.ours.to_dict() if self.ours else None,
            "theirs": self.theirs.to_dict() if self.theirs else None,
        }
        if self.orig_row_ours is not None:
            d["orig_row_ours"] = self.orig_row_ours
        if self.orig_row_theirs is not None:
            d["orig_row_theirs"] = self.orig_row_theirs
        return d


class SheetDiffKind(str, Enum):
    UNCHANGED = "unchanged"
    MODIFIED = "modified"
    ADDED_OURS = "added_ours"
    ADDED_THEIRS = "added_theirs"
    DELETED_BOTH = "deleted_both"


@dataclass
class SheetDiff:
    sheet_name: str
    kind: SheetDiffKind
    cells: dict[tuple[int, int], CellDiff] = field(default_factory=dict)
    max_row: int = 0
    max_col: int = 0
    conflict_count: int = 0
    key_col: int | None = None
    aligned: bool = False
    row_mapping: list[dict] | None = None
    col_mapping: list[dict] | None = None
    col_aligned: bool = False
    header_count: int = 0

    def to_dict(self) -> dict:
        cells_dict = {}
        for (r, c), cell_diff in self.cells.items():
            cells_dict[f"{r},{c}"] = cell_diff.to_dict()
        d = {
            "kind": self.kind.value,
            "max_row": self.max_row,
            "max_col": self.max_col,
            "conflict_count": self.conflict_count,
            "cells": cells_dict,
            "key_col": self.key_col,
            "aligned": self.aligned,
            "col_aligned": self.col_aligned,
        }
        if self.row_mapping:
            d["row_mapping"] = self.row_mapping
        if self.col_mapping:
            d["col_mapping"] = self.col_mapping
        return d


@dataclass
class WorkbookDiff:
    sheets: dict[str, SheetDiff] = field(default_factory=dict)
    sheet_order: list[str] = field(default_factory=list)
    conflict_count: int = 0
    auto_resolved_count: int = 0

    def to_dict(self) -> dict:
        return {
            "sheet_order": self.sheet_order,
            "sheets": {name: sd.to_dict() for name, sd in self.sheets.items()},
            "conflict_count": self.conflict_count,
            "auto_resolved_count": self.auto_resolved_count,
        }


class ResolutionChoice(str, Enum):
    ACCEPT_OURS = "ours"
    ACCEPT_THEIRS = "theirs"
    MANUAL = "manual"
    KEEP_BOTH = "keep_both"
    DELETE = "delete"


@dataclass
class CellResolution:
    sheet: str
    row: int
    col: int
    choice: ResolutionChoice
    manual_value: str | None = None
