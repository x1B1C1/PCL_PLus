# 自定义更新源

这是「换更新源」的增量能力：启动器可以不再问 GitHub，改问你自己的一台 HTTP 服务器。

没有配置这个地址的机器，行为与以前完全一致——它排在源列表最前面，但 `IsAvailable()` 返回 `false` 时会被直接跳过。

## 开关

配置键 `SystemUpdateCustomSource`（`Config.Update.CustomSource`，默认空字符串）。

| 填法 | 客户端实际请求 |
|---|---|
| `https://example.com/pcl` | `https://example.com/pcl/latest.json` |
| `https://example.com/pcl/latest.json` | 原样请求 |

列表里的其它源（OTA 仓的 `update.json`、GitHub Releases）仍然保留，作为这个源失败时的兜底。

## 清单格式

一个 UTF-8 编码的 JSON 对象：

```json
{
  "version": "4.0.5",
  "code": 42,
  "url": "https://example.com/pcl/PCL_Plus_v4.0.5_x64.exe",
  "sha256": "5f79d325d740fc326d19016d38a6ec161e02cb0e4eb9ca7dd9952ca97f6b1999",
  "changelog": "修了几个联机相关的问题。"
}
```

| 字段 | 必填 | 说明 |
|---|---|---|
| `version` | 是 | 版本号。允许 `4.0.5`、`v4.0.5`、`4.0`（缺段自动补零）、`4.0.5-Beta1` |
| `url` | 是 | 安装包地址，支持 `.exe` 或 `.zip`（zip 里取第一个 `.exe`） |
| `code` | 否 | 版本代号，缺省 0。只用于展示，判断新旧看 `version` |
| `sha256` | 否 | 文件校验值，目前只记录不强制校验 |
| `changelog` | 否 | 更新说明，会显示在更新提示窗口里 |

## 行为约定

- 请求超时 20 秒。
- 清单缓存 60 秒；用户手动点「重新检查」时跳过缓存。
- 只要有 `version` 与 `url`，就算清单合法；其余字段缺失不报错。
- 版本比较走 `UpdateVersion.IsNewer`，与内置源同一套规则（`4.0.5-Beta1` 低于 `4.0.5`）。
- 本源不提供公告，公告仍由后续源返回。

## 本地自测

在任意目录放一个 `latest.json`，起一个静态服务器：

```powershell
python -m http.server 8000
```

把 `SystemUpdateCustomSource` 填成 `http://127.0.0.1:8000`，然后在「设置 → 软件更新」里手动检查。

配置存在 `%APPDATA%\PCL_Plus\config.v1.json`（便携模式在 `PCL\config.v1.json`），键名是 `SystemUpdateCustomSource`。
