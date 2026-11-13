"""打包源码 zip + 建 Release + 上传资产（复用 _gh.py 的 token 读取）。

    python _release.py zip <src_dir> <out_zip>
    python _release.py release <owner/repo> <tag> <target_branch> <title> <body_file> [--pre] [--assets a,b,c]
"""
import os
import sys
import zipfile
import json
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import api as gh  # noqa: E402

SKIP_DIRS = {"bin", "obj", ".git", ".vs", "publish-x64", "release", "release-assets",
             "SRC-Backups", ".build-out", "node_modules", ".dist", "publish-x64-dir"}
SKIP_EXT = {".mp4", ".pdb", ".user"}


def do_zip(src, out):
    src = os.path.abspath(src)
    out = os.path.abspath(out)

    # 输出文件如果落在被压缩的树里，os.walk 会把它自己也读进去（边写边读，无限增长）。
    # 直接拒绝，比让人对着一个 10 GB 的 zip 发呆强。
    if out.startswith(src + os.sep):
        raise SystemExit(f"输出文件不能放在被压缩的目录里：{out}")

    n = 0
    total = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for root, dirs, files in os.walk(src):
            dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
            for f in files:
                if os.path.splitext(f)[1].lower() in SKIP_EXT:
                    continue
                p = os.path.join(root, f)
                arc = os.path.relpath(p, src)
                z.write(p, arc)
                n += 1
                total += os.path.getsize(p)
    print(f"{out}  文件 {n} 个  原始 {total/1048576:.2f} MB  压缩后 {os.path.getsize(out)/1048576:.2f} MB")


def upload_asset(upload_url, path):
    name = os.path.basename(path)
    url = f"{upload_url}?name={urllib.parse.quote(name)}"
    with open(path, "rb") as f:
        data = f.read()
    req = urllib.request.Request(url, data=data, method="POST", headers={
        "Authorization": f"token {gh.TOKEN}",
        "Accept": "application/vnd.github+json",
        "Content-Type": "application/octet-stream",
        "Content-Length": str(len(data)),
        "User-Agent": "PCL_Plus-agent",
    })
    try:
        with urllib.request.urlopen(req, timeout=600) as r:
            return r.status, json.loads(r.read())
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def main():
    a = sys.argv[1:]
    if a[0] == "zip":
        do_zip(a[1], a[2])
        return

    if a[0] == "release":
        repo, tag, branch, title, body_file = a[1], a[2], a[3], a[4], a[5]
        pre = "--pre" in a
        assets = []
        if "--assets" in a:
            assets = [x for x in a[a.index("--assets") + 1].split(",") if x]
        body = open(body_file, encoding="utf-8").read() if body_file != "-" else ""
        payload = {
            "tag_name": tag, "target_commitish": branch, "name": title,
            "body": body, "draft": False, "prerelease": pre,
        }
        st, r = gh.call("POST", f"/repos/{repo}/releases", payload)
        print("create release:", st)
        if st >= 300:
            print(r)
            return
        print("  release id", r["id"], r["html_url"])
        up = r["upload_url"].split("{")[0]
        for p in assets:
            st2, r2 = upload_asset(up, p)
            if st2 >= 300:
                print(f"  [FAIL] {os.path.basename(p)} -> {st2} {r2[:200]}")
            else:
                print(f"  [OK] {os.path.basename(p)} {r2['size']/1048576:.2f} MB  state={r2['state']}")
        return

    if a[0] == "replace":
        # 同一 tag 换资产：先删同名旧资产，再传新的
        repo, tag = a[1], a[2]
        assets = [x for x in a[3:] if not x.startswith("--")]
        st, rel = gh.call("GET", f"/repos/{repo}/releases/tags/{tag}")
        print("get release:", st)
        if st >= 300:
            print(rel)
            return
        up = rel["upload_url"].split("{")[0]
        existing = {x["name"]: x["id"] for x in rel["assets"]}
        for p in assets:
            name = os.path.basename(p)
            if name in existing:
                st2, _ = gh.call("DELETE", f"/repos/{repo}/releases/assets/{existing[name]}")
                print(f"  delete {name}: {st2}")
            st3, r3 = upload_asset(up, p)
            if st3 >= 300:
                print(f"  [FAIL] {name} -> {st3} {str(r3)[:200]}")
            else:
                print(f"  [OK] {name} {r3['size']/1048576:.2f} MB")
        return

    print(__doc__)


if __name__ == "__main__":
    main()
