"""极简 GitHub API 助手：从 Windows 凭据管理器取 GitHub Desktop 的 token，不落盘。

用法：
    python _gh.py whoami
    python _gh.py repos
    python _gh.py create <name> [--private] [--desc "..."]
    python _gh.py branch <repo> <branch> [--from <branch>]
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import sys
import urllib.request
import urllib.error

TARGET = "GitHub - https://api.github.com/x1B1C1"
API = "https://api.github.com"


class CREDENTIALW(ctypes.Structure):
    _fields_ = [
        ("Flags", wt.DWORD),
        ("Type", wt.DWORD),
        ("TargetName", wt.LPWSTR),
        ("Comment", wt.LPWSTR),
        ("LastWritten", wt.FILETIME),
        ("CredentialBlobSize", wt.DWORD),
        ("CredentialBlob", ctypes.POINTER(ctypes.c_byte)),
        ("Persist", wt.DWORD),
        ("AttributeCount", wt.DWORD),
        ("Attributes", ctypes.c_void_p),
        ("TargetAlias", wt.LPWSTR),
        ("UserName", wt.LPWSTR),
    ]


def token() -> str:
    advapi32 = ctypes.WinDLL("advapi32", use_last_error=True)
    p = ctypes.POINTER(CREDENTIALW)()
    ok = advapi32.CredReadW(ctypes.c_wchar_p(TARGET), 1, 0, ctypes.byref(p))
    if not ok:
        raise SystemExit(f"读不到凭据 {TARGET}，错误码 {ctypes.get_last_error()}")
    try:
        c = p.contents
        raw = ctypes.string_at(c.CredentialBlob, c.CredentialBlobSize)
        return raw.decode("utf-8")
    finally:
        advapi32.CredFree(p)


TOKEN = token()


def call(method: str, path: str, payload=None, raw=False):
    url = path if path.startswith("http") else API + path
    data = None
    headers = {
        "Authorization": f"token {TOKEN}",
        "Accept": "application/vnd.github+json",
        "User-Agent": "PCL_Plus-agent",
        "X-GitHub-Api-Version": "2022-11-28",
    }
    if payload is not None:
        data = json.dumps(payload).encode()
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            body = r.read()
            return (r.status, body if raw else (json.loads(body) if body else None))
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        return (e.code, body)


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    cmd = args[0]

    if cmd == "whoami":
        st, u = call("GET", "/user")
        print("status", st)
        if st == 200:
            print("login     :", u["login"])
            print("name      :", u["name"])
            print("public    :", u["public_repos"], " private:", u["total_private_repos"])
            print("scopes    : 见响应头（token 权限）")
        else:
            print(u)

    elif cmd == "repos":
        st, r = call("GET", "/user/repos?per_page=100&affiliation=owner")
        print("status", st)
        if st == 200:
            print(f"共 {len(r)} 个仓库")
            for x in r:
                print(f"  {x['full_name']:<40} private={x['private']!s:<5} default={x['default_branch']:<6} size={x['size']}KB")
        else:
            print(r)

    elif cmd == "create":
        name = args[1]
        private = "--private" in args
        desc = ""
        if "--desc" in args:
            desc = args[args.index("--desc") + 1]
        st, r = call("POST", "/user/repos", {
            "name": name, "description": desc, "private": private,
            "has_issues": True, "has_wiki": False, "has_projects": False,
            "auto_init": False,
        })
        print("status", st)
        print(r if st >= 300 else f"已创建 {r['full_name']}  clone={r['clone_url']}")

    elif cmd == "branches":
        st, r = call("GET", f"/repos/x1B1C1/{args[1]}/branches")
        print("status", st)
        if st == 200:
            for b in r:
                print(f"  {b['name']}  {b['commit']['sha'][:10]}")
            if not r:
                print("  (空仓库)")
        else:
            print(r)

    elif cmd == "delete":
        name = args[1]
        st, r = call("DELETE", f"/repos/x1B1C1/{name}")
        print("status", st, "" if st == 204 else r)

    else:
        print("未知命令", cmd)


if __name__ == "__main__":
    main()
