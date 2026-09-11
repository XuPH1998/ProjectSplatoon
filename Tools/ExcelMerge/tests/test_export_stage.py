from pathlib import Path

import openpyxl

from excelcompare.git.export_stage import _looks_like_lfs_pointer, create_empty_workbook


def test_lfs_pointer_detection():
    pointer = (
        b"version https://git-lfs.github.com/spec/v1\n"
        b"oid sha256:473dd2b029bad6b15ae0110067ffedcf0538d214a2c6ac937fd3a28c043296bc\n"
        b"size 10227\n"
    )

    assert _looks_like_lfs_pointer(pointer)
    assert not _looks_like_lfs_pointer(b"not an lfs pointer")


def test_create_empty_workbook_writes_valid_xlsx(tmp_path: Path):
    output_path = tmp_path / "empty.xlsx"

    create_empty_workbook(output_path)

    workbook = openpyxl.load_workbook(output_path)
    assert workbook.sheetnames == ["Sheet"]
    workbook.close()
