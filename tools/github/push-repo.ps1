# 把本地源码树推到 GitHub，token 从 Windows 凭据管理器现取，不落盘。
# 用法： powershell -ExecutionPolicy Bypass -File _push.ps1 -Src <目录> -Repo <owner/name> -Branch <分支> -Message <提交说明>
param(
    [Parameter(Mandatory = $true)][string]$Src,
    [Parameter(Mandatory = $true)][string]$Repo,
    [Parameter(Mandatory = $true)][string]$Branch,
    [Parameter(Mandatory = $true)][string]$Message,
    [string]$Stage = ''
)

$ErrorActionPreference = 'Stop'

# --- 取 token ---
$code = @'
using System;
using System.Runtime.InteropServices;
public static class CredPush {
    [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);
    [StructLayout(LayoutKind.Sequential)]
    struct CREDENTIAL { public int Flags; public int Type; public IntPtr TargetName; public IntPtr Comment;
        public long LastWritten; public int CredentialBlobSize; public IntPtr CredentialBlob;
        public int Persist; public int AttributeCount; public IntPtr Attributes;
        public IntPtr TargetAlias; public IntPtr UserName; }
    public static string Get(string target) {
        IntPtr p; if (!CredRead(target, 1, 0, out p)) return null;
        try { CREDENTIAL c = (CREDENTIAL)Marshal.PtrToStructure(p, typeof(CREDENTIAL));
              byte[] b = new byte[c.CredentialBlobSize];
              Marshal.Copy(c.CredentialBlob, b, 0, b.Length);
              return System.Text.Encoding.UTF8.GetString(b);
        } finally { CredFree(p); }
    }
}
'@
if (-not ('CredPush' -as [type])) { Add-Type -TypeDefinition $code }
$tok = [CredPush]::Get('GitHub - https://api.github.com/x1B1C1')
if (-not $tok) { throw '读不到 GitHub 凭据' }
$basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$tok"))

if (-not $Stage) { $Stage = Join-Path $env:TEMP ("push-" + [guid]::NewGuid().ToString('N').Substring(0, 8)) }
if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Path $Stage | Out-Null

Write-Host "[1/5] 复制源码到 $Stage"
$null = robocopy $Src $Stage /E /NFL /NDL /NJH /NJS /NP `
    /XD bin obj .git .vs publish-x64 release release-assets SRC-Backups .build-out `
    /XF *.mp4 *.pdb *.user
if ($LASTEXITCODE -ge 8) { throw "robocopy 失败，退出码 $LASTEXITCODE" }

Write-Host '[2/5] git init'
Push-Location $Stage
try {
    git init -q -b $Branch
    git config user.name  'x1B1C1'
    git config user.email '347430791+x1B1C1@users.noreply.github.com'
    git config core.autocrlf false
    git config core.safecrlf false

    Write-Host '[3/5] git add + commit'
    git add -A
    git commit -q -m $Message
    if ($LASTEXITCODE -ne 0) { throw 'commit 失败' }
    Write-Host ("    文件数 " + (git ls-files | Measure-Object).Count)

    Write-Host "[4/5] push 到 https://github.com/$Repo 分支 $Branch"
    $ErrorActionPreference = 'Continue'
    $pushOut = git -c http.extraheader="AUTHORIZATION: basic $basic" `
        push --force "https://github.com/$Repo.git" "HEAD:refs/heads/$Branch" 2>&1
    $pushCode = $LASTEXITCODE
    $pushOut | ForEach-Object { "    $_" }
    if ($pushCode -ne 0) { throw "push 失败，退出码 $pushCode" }
    Write-Host '[5/5] 完成'
    git log --oneline -1
}
finally { Pop-Location }
