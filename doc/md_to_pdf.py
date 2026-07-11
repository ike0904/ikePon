#!/usr/bin/env python3
"""
ikePon マニュアル MD → HTML → PDF 変換スクリプト
使い方: python md_to_pdf.py
"""

import subprocess
import shutil
import sys
import time
from pathlib import Path

DOC_DIR   = Path(__file__).parent
MD_FILE   = DOC_DIR / "ikePon_manual.md"
HTML_FILE = DOC_DIR / "ikePon_manual.html"
PDF_FILE  = DOC_DIR / "ikePon_manual.pdf"
CSS_FILE  = DOC_DIR / "manual.css"

TMP_PDF = Path(r"C:\Users\ike09\AppData\Local\Temp\ikepon_manual.pdf")

CHROME_PATHS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]


def find_chrome() -> Path:
    for p in CHROME_PATHS:
        path = Path(p)
        if path.exists():
            return path
    raise FileNotFoundError("Chrome が見つかりません。インストールを確認してください。")


def step_md_to_html():
    print("[1/2] MD → HTML 変換中 (pandoc)...")
    result = subprocess.run(
        [
            "pandoc", "-s",
            "--css", str(CSS_FILE),
            "--self-contained",
            "-o", str(HTML_FILE),
            str(MD_FILE),
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode != 0:
        print("ERROR: pandoc 失敗")
        print(result.stderr)
        sys.exit(1)
    print(f"  -> {HTML_FILE.name} 生成完了")


def step_html_to_pdf():
    print("[2/2] HTML → PDF 変換中 (Chrome headless)...")
    chrome = find_chrome()

    # 日本語パス対策: 一時ファイルに出力してからコピー
    TMP_PDF.parent.mkdir(parents=True, exist_ok=True)
    if TMP_PDF.exists():
        TMP_PDF.unlink()

    html_url = "file:///" + HTML_FILE.as_posix()
    result = subprocess.run(
        [
            str(chrome),
            "--headless",
            "--disable-gpu",
            "--no-sandbox",
            f"--print-to-pdf={TMP_PDF}",
            "--print-to-pdf-no-header",
            html_url,
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
    )

    # Chrome headless は終了コードが不安定なため、ファイルの存在で判定
    deadline = time.time() + 15
    while time.time() < deadline:
        if TMP_PDF.exists() and TMP_PDF.stat().st_size > 0:
            break
        time.sleep(1)
    else:
        print("ERROR: PDF の生成がタイムアウトしました")
        if result.stderr:
            print(result.stderr)
        sys.exit(1)

    shutil.copy2(TMP_PDF, PDF_FILE)
    print(f"  -> {PDF_FILE.name} 生成完了")


if __name__ == "__main__":
    print(f"対象ファイル: {MD_FILE}")
    step_md_to_html()
    step_html_to_pdf()
    print("\n変換完了！")
    print(f"  HTML: {HTML_FILE}")
    print(f"  PDF : {PDF_FILE}")
