"""把仓库根目录的文档（许可证 + 三个 README）作为单个提交推到源码仓的 main。

刻意不使用 git push：本地仓库的 origin 可能指向上游，且工作区通常有一堆未提交的源码改动，
push 会把整份源码历史（或半成品）一起推上去。这里只走 Git Data API，只影响列出的这几个文件。

注意：文档属于**源码仓**（x1B1C1/PCL_Plus）；发布包与更新清单在 OTA 仓
（x1B1C1/PCL_Plus_OTA），那边的 update.json 由发布流程维护，不由本脚本推送。

用法：
    python tools/push-docs.py                 # 推到默认源码仓
    python tools/push-docs.py --dry-run       # 只打印将要推送的文件与大小
    python tools/push-docs.py --owner X --repo Y --files README.md docs/FAQ.md

依赖：已登录的 gh（`gh auth status` 能过）。
"""
import argparse
import base64
import json
import os
import subprocess
import sys

# ── 默认目标：源码仓 ────────────────────────────────────────────────
OWNER = "x1B1C1"
REPO = "PCL_Plus"
BRANCH = "main"
MESSAGE = "docs: 更新许可证与 README"

# 脚本位于 <repo>/tools/，所以仓库根 = 上一级目录
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FILES = ["LICENSE", "LICENSE-APACHE", "README.md", "README-EN.md", "README-ZH_TW.md"]


def gh(method, path, payload=None):
    """调用 `gh api`，返回解析后的 JSON（空响应返回 {}）。"""
    cmd = ["gh", "api", "--method", method, path]
    if payload is not None:
        cmd += ["--input", "-"]
    proc = subprocess.run(
        cmd,
        input=json.dumps(payload).encode() if payload is not None else None,
        capture_output=True,
    )
    if proc.returncode != 0:
        print(f"[FAIL] {method} {path}\n{proc.stderr.decode('utf-8', 'ignore')}")
        sys.exit(1)
    out = proc.stdout.decode("utf-8", "ignore").strip()
    return json.loads(out) if out else {}


def main():
    parser = argparse.ArgumentParser(description="把仓库根目录的文档推到源码仓 main")
    parser.add_argument("--owner", default=OWNER)
    parser.add_argument("--repo", default=REPO)
    parser.add_argument("--branch", default=BRANCH)
    parser.add_argument("--message", default=MESSAGE)
    parser.add_argument("--root", default=ROOT, help="本地仓库根目录")
    parser.add_argument("--files", nargs="*", default=FILES, help="要推送的相对路径")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    missing = [f for f in args.files if not os.path.isfile(os.path.join(args.root, f))]
    if missing:
        print(f"[FAIL] 本地找不到这些文件：{', '.join(missing)}")
        sys.exit(1)

    repo = f"{args.owner}/{args.repo}"
    print(f"目标：{repo}@{args.branch}  根目录：{args.root}")
    for f in args.files:
        print(f"  {f:20s} {os.path.getsize(os.path.join(args.root, f)):8d} B")
    if args.dry_run:
        print("（dry-run，未推送）")
        return

    ref = gh("GET", f"repos/{repo}/git/ref/heads/{args.branch}")
    parent = ref["object"]["sha"]
    base_tree = gh("GET", f"repos/{repo}/git/commits/{parent}")["tree"]["sha"]
    print(f"parent={parent[:10]} base_tree={base_tree[:10]}")

    tree = []
    for name in args.files:
        with open(os.path.join(args.root, name), "rb") as f:
            raw = f.read()
        blob = gh("POST", f"repos/{repo}/git/blobs",
                  {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
        tree.append({"path": name, "mode": "100644", "type": "blob", "sha": blob["sha"]})
        print(f"  blob {name:20s} {len(raw):8d} B  {blob['sha'][:10]}")

    new_tree = gh("POST", f"repos/{repo}/git/trees",
                  {"base_tree": base_tree, "tree": tree})["sha"]
    commit = gh("POST", f"repos/{repo}/git/commits",
                {"message": args.message, "tree": new_tree, "parents": [parent]})["sha"]
    gh("PATCH", f"repos/{repo}/git/refs/heads/{args.branch}", {"sha": commit})
    print(f"OK commit={commit}")


if __name__ == "__main__":
    main()
