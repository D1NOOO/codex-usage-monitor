<p align="center">
  <img src="assets/logo.png" alt="Codex 用量监视器项目图标" width="180">
</p>

# Codex 用量监视器（Windows）

**简体中文** · [繁體中文](README.zh-TW.md) · [English](README.md)

一个原生 Windows 托盘小工具，在 ChatGPT 桌面端的 Codex 界面旁显示当前 5 小时与
7 天用量窗口，并展示可用的限额重置券。

> [!IMPORTANT]
> 非官方社区项目，与 OpenAI 无隶属关系。`codex app-server` 是本地实验性协议，
> 未来版本可能调整。

## 界面截图

悬浮条支持 1 行与多行两种布局。多行布局中的重置券卡片会按到期时间分级变色：
正常 → 警告（≤7 天）→ 危险染色（≤3 天），无券时自动隐藏。

![多行布局](docs/overlay-multirow-credits-zh-cn.png)



1 行布局把 5 小时、7 天与重置券卡片横向排成一行，适合嵌入标题栏。

![1 行布局](docs/overlay-oneline-credits-zh-cn.png)



鼠标悬停托盘图标可查看分行显示的用量与重置券摘要。

![托盘悬浮提示](docs/tray-tooltip-zh-cn.png)



外观设置提供样式预览：位置、字体、颜色、透明度，以及「显示重置券（只读查询）」开关。预览中的用量、时间和重置券数量为演示数据。
悬浮框会实时跟随 Windows 显示缩放，「整体缩放」在此基础上叠加。预览空间足够时按实际尺寸显示，空间不足时会标明缩小比例。
整体缩放默认新的 100%（对应原来 85% 的尺寸），可在「外观设置 → 排版与尺寸 → 整体缩放」调整为 50%–200%。字体、间距与圆角一起缩放，旧配置更新后保持实际大小。

![外观设置](docs/appearance-settings-reset-credits-zh-cn.png)

## 功能

- 悬浮条支持桌面悬浮（置顶、可拖拽、点击穿透）与窗口吸附两种模式，
  1 行 / 多行两种布局，剩余 / 已用两种显示口径。
- 重置券卡片：只读展示可用数量与最早到期时间，绝不调用核销接口。
- 托盘悬浮提示分行显示用量与重置券摘要。
- 周期刷新复用常驻 app-server，窗口最小化时自动降频（见下文「流量消耗」）。
- 其余：三语界面（简中 / 繁中 / 英文）、完整外观编辑、开机启动、
  后台更新检测与校验式原地更新。

## 运行要求

- Windows 10/11，.NET Framework 4.8。
- 已登录的 ChatGPT 桌面端（旧版 Codex App + Codex CLI 仍兼容）。
  API Key 登录无法显示订阅用量窗口。

## 安装

1. 从 [Releases](https://github.com/D1NOOO/codex-usage-monitor/releases) 下载
   `CodexRateMonitor-VERSION-windows-x64.zip`，按需校验 `SHA256SUMS.txt`。
2. 解压到固定位置，运行 `CodexRateMonitor.exe`。程序无主窗口，常驻通知区域
   （可能先被收进 `^` 隐藏区）。右键托盘图标配置，双击打开外观设置。

EXE 未做商业签名，SmartScreen 可能提示未知发布者；请只从 Releases 下载并校验。

## 流量消耗

程序专门为低流量设计（背景见
[issue #5](https://github.com/D1NOOO/codex-usage-monitor/issues/5)）：

| 场景 | 行为 |
|---|---|
| ChatGPT/Codex 位于前台 | 每 `ForegroundRefreshSeconds`（默认 30s）一次轻量读取 |
| 窗口可见但位于后台 | 每 `RefreshSeconds`（默认 60s）一次读取 |
| 窗口最小化 / 仅托盘 | 降频到 `MinimizedRefreshSeconds`（默认 300s） |
| 重置券查询 | 每 `ResetCreditsSeconds`（默认 30 分钟）一次只读请求 |

app-server 进程全程常驻复用：先以 `account/read`（`refreshToken: false`）确认本地账号，
再发送 `account/rateLimits/read`，并接收监视器自身连接的 `account/rateLimits/updated`
通知。切回前台、恢复窗口、系统唤醒后等待 1 秒合并补刷；同时触发的刷新复用一个
请求，30 秒超时，失败后逐步退避到最多 5 分钟。额度连续相同不会触发重启。
悬浮条与托盘提示显示上次确认更新的时间，账号变化时清除旧数据。
这能减少轮询等待，但无法保证与官方界面独立缓存的数据逐秒一致。详见
[刷新机制与回归用例](docs/usage-refresh.md)。app-server 也可能自行发出
后台请求，因此进程总流量可能高于用量读取本身。

如果 app-server 报告令牌失效，悬浮条会立即清除旧用量。连续失败时最多自动重启
app-server 一次；若登录仍无效，程序停止自动重启，恢复探测间隔不短于 5 分钟。
通过 ChatGPT/Codex 重新登录后，可点击托盘的「立即刷新」马上重试。

## 实现原理

```mermaid
flowchart LR
    UI["托盘图标 + 穿透式悬浮条"] --> Client["AppServerClient"]
    Client -->|"stdin/stdout 上逐行 JSON"| Server["codex app-server"]
    Server --> Auth["ChatGPT/Codex 自己管理登录凭据"]
    Server --> API["OpenAI 服务"]
```

程序按「桌面端内置 → npm 安装 → PATH」的顺序寻找 Codex 可执行文件，启动
`codex.exe app-server`，发送 `account/rateLimits/read`，将 `primary` 渲染为 5 小时窗口、
`secondary` 为 7 天窗口，并合并 `account/rateLimits/updated` 推送。

登录、令牌刷新和与 OpenAI 的通信全部由 ChatGPT/Codex 负责，本工具不实现认证。
唯一例外：启用重置券时，程序本地读取 `~/.codex/auth.json` 的访问令牌，仅用于查询只读接口
`chatgpt.com/backend-api/wham/rate-limit-reset-credits`——令牌只在单次请求期间存于内存，
绝不核销、不存储、不外传。诊断日志写入 `%LOCALAPPDATA%\CodexRateMonitor\logs`
并自动清理，不含令牌与账户信息。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

诊断默认关闭。排查时将 `settings.json` 中的 `DiagnosticsEnabled` 改为 `true`，
重启监视器后生效。日志默认保留 7 天，单文件约 2 MiB 时轮转，总容量预算 20 MiB；
关闭诊断后，只要监视器仍在运行，也会清理过期日志。刷新原因、请求耗时、通知处理
以及重置时间补刷规则见 [用量刷新与诊断说明](docs/usage-refresh.md)。

## 配置

`settings.json`（来自 `config/settings.default.json`）常用字段：

| 字段                                               | 说明                                 |
| ------------------------------------------------ | ---------------------------------- |
| `Language`                                       | `auto` / `zh-CN` / `zh-TW` / `en`  |
| `OverlayMode`                                    | `desktop`（默认，桌面悬浮）/ `attach`（吸附窗口） |
| `UsageDisplay`                                   | `remaining`（默认）/ `used`            |
| `ForegroundRefreshSeconds`                       | 30–`RefreshSeconds`，前台刷新间隔（默认 30）     |
| `RefreshSeconds`                                 | 30–900，窗口可见但位于后台时的刷新间隔（默认 60） |
| `MinimizedRefreshSeconds`                        | 60–3600，最小化时的刷新间隔（默认 300）          |
| `ShowResetCredits`                               | 重置券卡片开关（默认开）                       |
| `ResetCreditsSeconds`                            | 300–86400，重置券查询间隔（默认 1800）         |
| `DiagnosticsEnabled` / `DiagnosticRetentionDays` | 诊断默认关闭，默认保留 7 天（1–30），总容量预算 20 MiB |

其余外观字段（字体、颜色 `#RRGGBB`、缩放、透明度等）见默认模板或外观设置界面。

整体缩放范围为 50%–200%，新的 100% 使用原来 85% 的紧凑尺寸。旧配置在内存中换算，
保持实际大小不变：旧 85% 对应新 100%，旧 100% 对应新约 117.65%。更新器保留已有
`settings.json`，保存设置时写入 `Style.ScaleBasisVersion: 2`，避免重复换算；
字体、颜色、透明度、位置及其他配置继续保留。

## 构建与发布

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\build.ps1 -Package
```

输出位于 `artifacts/`。CI 先运行 `scripts/verify.ps1`（三语键、JSON/PowerShell 语法、
隐私与凭据扫描）。发布时更新 `version.txt`、推送同名 tag，
`.github/workflows/release.yml` 自动构建并创建带来源证明的 Release。

## FAQ

### 找不到 Codex 可执行文件 / 未登录

先打开或更新 ChatGPT 桌面端；独立 CLI 用户用 `codex --version` 与
`codex app-server --help` 确认可用，`codex doctor` 排查登录。若为 API Key 登录态，
请改用 ChatGPT 账户登录——API Key 无法返回订阅用量窗口。

### 悬浮条不显示

把 ChatGPT/Codex 窗口切到前台（吸附模式只在窗口可见时显示）；确认托盘图标在运行；
必要时点击「立即刷新」。

### UI 更新后位置偏了

位置偏移适配当前桌面端布局，界面更新后可能需要调整。欢迎提交带脱敏截图的 issue。

## 已知限制

仅支持 Windows；依赖本地实验性 app-server 协议；Release EXE 未做代码签名。

## 许可证

[MIT](LICENSE)。Codex 和 OpenAI 是其权利人的商标，本项目为非官方项目。
