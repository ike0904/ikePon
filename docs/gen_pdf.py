#!/usr/bin/env python3
"""
マニュアル MD -> HTML -> PDF 変換スクリプト
使い方: python gen_pdf.py

同じフォルダにある *_manual.md を自動検出して処理します。
該当ファイルが 0 個または 2 個以上の場合はエラーになります。
"""

import re
import subprocess
import shutil
import sys
import time
from pathlib import Path

DOC_DIR = Path(__file__).parent

# *_manual.md を自動検出
def _discover_md() -> Path:
    candidates = list(DOC_DIR.glob("*_manual.md"))
    if len(candidates) == 0:
        print("ERROR: *_manual.md が見つかりません。")
        sys.exit(1)
    if len(candidates) > 1:
        names = ", ".join(f.name for f in sorted(candidates))
        print(f"ERROR: *_manual.md が複数見つかりました: {names}")
        sys.exit(1)
    return candidates[0]

MD_FILE   = _discover_md()
APP_NAME  = MD_FILE.stem.replace("_manual", "")
HTML_FILE = DOC_DIR / f"{APP_NAME}_manual.html"
PDF_FILE  = DOC_DIR / f"{APP_NAME}_manual.pdf"
CSS_FILE  = DOC_DIR / "manual.css"
TMP_PDF   = Path(r"C:\Users\ike09\AppData\Local\Temp") / f"{APP_NAME}_manual.pdf"

CHROME_PATHS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]

EN_MARKER = "<!-- EN_START -->"

# ── リリース用フォルダ ──────────────────────────────────────────────────────────
# None の場合は DOC_DIR.parent/"dist" を自動検索。
# アプリごとに絶対パス or DOC_DIR 起点の相対パスを指定して上書きする。
DIST_DIR = DOC_DIR.parent / "src" / "ikePon" / "bin" / "Release" / "net9.0-windows" / "win-x64" / "publish"


def substitute_en_images(content: str) -> str:
    """英語セクション内の画像を _en 版に差し替える（存在する場合のみ）。"""
    if EN_MARKER not in content:
        return content

    ja_part, en_part = content.split(EN_MARKER, 1)

    def resolve_en(src: str) -> str:
        if src.startswith("http://") or src.startswith("https://"):
            return src
        p = (DOC_DIR / src).resolve()
        en_p = p.parent / f"{p.stem}_en{p.suffix}"
        if en_p.exists():
            try:
                return str(en_p.relative_to(DOC_DIR.resolve())).replace("\\", "/")
            except ValueError:
                pass
        return src

    # Markdown 画像: ![alt](src)
    en_part = re.sub(
        r"(!\[[^\]]*\]\()([^)]+)(\))",
        lambda m: m.group(1) + resolve_en(m.group(2)) + m.group(3),
        en_part,
    )
    # HTML img タグ: src="..."
    en_part = re.sub(
        r'(<img\b[^>]*?\bsrc=")([^"]+)(")',
        lambda m: m.group(1) + resolve_en(m.group(2)) + m.group(3),
        en_part,
    )

    return ja_part + en_part  # マーカー自体は除去


def get_page_title(content: str) -> str:
    """マークダウン内の最初の # 見出しをページタイトルとして返す。"""
    for line in content.splitlines():
        line = line.strip()
        if line.startswith("# "):
            return line[2:].strip()
    return f"{APP_NAME} manual"


def find_chrome() -> Path:
    for p in CHROME_PATHS:
        path = Path(p)
        if path.exists():
            return path
    raise FileNotFoundError("Chrome が見つかりません。インストールを確認してください。")


def step_md_to_html():
    print("[1/2] MD -> HTML 変換中 (pandoc)...")
    content = MD_FILE.read_text(encoding="utf-8")
    content = substitute_en_images(content)
    page_title = get_page_title(content)
    result = subprocess.run(
        [
            "pandoc", "-s",
            "--css", str(CSS_FILE),
            "--self-contained",
            "--from=markdown+raw_html",
            "--metadata", f"pagetitle={page_title}",
            "-o", str(HTML_FILE),
            "-",  # stdin から読み込む
        ],
        input=content,
        capture_output=True,
        text=True,
        encoding="utf-8",
        cwd=str(DOC_DIR),
    )
    if result.returncode != 0:
        print("ERROR: pandoc 失敗")
        print(result.stderr)
        sys.exit(1)
    print(f"  -> {HTML_FILE.name} 生成完了")


def step_html_to_pdf():
    print("[2/2] HTML -> PDF 変換中 (Chrome headless)...")
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
    deadline = time.time() + 20
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


def cleanup():
    if HTML_FILE.exists():
        HTML_FILE.unlink()


if __name__ == "__main__":
    print(f"対象ファイル: {MD_FILE.name}")
    step_md_to_html()
    step_html_to_pdf()
    cleanup()

    # リリースフォルダにコピー（DIST_DIR 未設定なら ../dist/ を自動検索）
    _dist = DIST_DIR if DIST_DIR is not None else (DOC_DIR.parent / "dist")
    if _dist.exists():
        dest = _dist / PDF_FILE.name
        shutil.copy2(PDF_FILE, dest)
        print(f"  -> リリースフォルダにコピー: {dest}")

    print("\n変換完了！")
    print(f"  PDF : {PDF_FILE}")
