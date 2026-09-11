from __future__ import annotations

import click

from excelcompare.core.diff import diff_workbooks
from excelcompare.core.excel_io import load_workbook
from excelcompare.core.merge import auto_merge, save_merged_with_styles, unresolved_count
from excelcompare.core.models import DiffKind, WorkbookData


@click.group()
def cli():
    """ExcelCompare — Excel 3-way merge tool."""


@cli.command()
@click.argument("file1", type=click.Path(exists=True))
@click.argument("file2", type=click.Path(exists=True))
def diff(file1: str, file2: str):
    """Compare two Excel files."""
    wb1 = load_workbook(file1)
    wb2 = load_workbook(file2)

    result = diff_workbooks(wb1, wb1, wb2)
    total_changes = 0

    for name in result.sheet_order:
        sd = result.sheets.get(name)
        if not sd or not sd.cells:
            click.echo(f"Sheet: {name}")
            click.echo("  (no changes)")
            continue

        click.echo(f"Sheet: {name}")
        for (row, col), cd in sorted(sd.cells.items()):
            col_letter = _col_to_letter(col)
            addr = f"{col_letter}{row}"
            old_val = cd.base.display() if cd.base else "(empty)"
            new_val = cd.theirs.display() if cd.theirs else "(empty)"
            click.echo(f"  CHANGED {addr}: {old_val} -> {new_val}")
            total_changes += 1

    click.echo(f"\n{total_changes} change(s) found.")


@cli.command()
@click.argument("base", type=click.Path(exists=True))
@click.argument("ours", type=click.Path(exists=True))
@click.argument("theirs", type=click.Path(exists=True))
@click.option("-o", "--output", type=click.Path(), default=None, help="Output file path")
@click.option("--visual", is_flag=True, help="Open web UI for visual merge")
@click.option("--strategy", type=click.Choice(["ours", "theirs"]), default=None,
              help="Bulk resolution strategy (non-interactive)")
def merge(base: str, ours: str, theirs: str, output: str | None, visual: bool, strategy: str | None):
    """3-way merge of Excel files."""
    base_wb = load_workbook(base)
    ours_wb = load_workbook(ours)
    theirs_wb = load_workbook(theirs)

    wb_diff = diff_workbooks(base_wb, ours_wb, theirs_wb)
    resolutions = auto_merge(wb_diff)

    total_changes = sum(len(sd.cells) for sd in wb_diff.sheets.values())
    click.echo(f"Total changes: {total_changes}")
    click.echo(f"Auto-resolved: {wb_diff.auto_resolved_count}")
    click.echo(f"Conflicts: {wb_diff.conflict_count}")

    has_changes = total_changes > 0

    if visual and has_changes:
        if strategy:
            from excelcompare.core.models import CellResolution, ResolutionChoice
            choice = ResolutionChoice.ACCEPT_OURS if strategy == "ours" else ResolutionChoice.ACCEPT_THEIRS
            for sheet_name, sd in wb_diff.sheets.items():
                for (row, col), cd in sd.cells.items():
                    if cd.kind == DiffKind.CONFLICT:
                        resolutions[(sheet_name, row, col)] = CellResolution(
                            sheet=sheet_name, row=row, col=col, choice=choice,
                        )
            click.echo(f"Resolved all conflicts with strategy: {strategy}")
        else:
            from excelcompare.web.app import run_merge_server
            run_merge_server(
                wb_diff, resolutions, base_wb, ours_wb, theirs_wb,
                output or "merged.xlsx",
                ours_path=ours, theirs_path=theirs,
            )
            return
    elif wb_diff.conflict_count > 0 and not visual:
        click.echo("Use --visual to resolve conflicts interactively, or --strategy=ours/theirs")
        if not output:
            return

    out_path = output or "merged.xlsx"
    save_merged_with_styles(wb_diff, resolutions, ours, theirs, out_path, theirs_wb)
    remaining = unresolved_count(wb_diff, resolutions)
    click.echo(f"Saved to: {out_path} (unresolved conflicts: {remaining})")


@cli.command()
@click.option("--global", "is_global", is_flag=True, help="Install to global git config")
def install(is_global: bool):
    """Configure git to use ExcelCompare as merge driver."""
    from excelcompare.git.install import install_git_driver
    install_git_driver(is_global)


def _col_to_letter(col: int) -> str:
    result = ""
    while col > 0:
        col, remainder = divmod(col - 1, 26)
        result = chr(65 + remainder) + result
    return result
