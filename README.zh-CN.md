<p align="center">
  <img src="assets/logo.png" alt="Codex 用量监视器项目图标" width="180">
</p>

# Codex 用量监视器（Windows）

**简体中文** · [繁體中文](README.zh-TW.md) · [English](README.md)

轻量 Windows 托盘工具，显示 Codex 额度、重置时间与重置券，支持桌面悬浮和窗口吸附。

> 非官方社区项目，与 OpenAI 无隶属关系。依赖实验性的本地 `codex app-server` 协议。

## 主要功能

- **额度一目了然**：显示 5 小时、7 天额度及剩余或已用比例，支持自动、全部显示和仅周用量面板。
- **灵活悬浮布局**：桌面模式置顶、可拖拽；窗口吸附模式点击穿透，支持单行或多行。
- **重置券只读查询**：展示可用数量与最早到期时间，不执行核销。
- **日常使用便捷**：实时外观预览、三语界面、开机启动及校验式原地更新。

## 安装与使用

需要 Windows 10/11、.NET Framework 4.8，以及使用 ChatGPT 账号登录的 ChatGPT 桌面端、Codex App 或 Codex CLI。API Key 登录不提供订阅额度。

1. 从 [Releases](https://github.com/D1NOOO/codex-usage-monitor/releases) 下载 `CodexRateMonitor-VERSION-windows-x64.zip`，使用 `SHA256SUMS.txt` 校验。
2. 解压到固定目录，运行 `CodexRateMonitor.exe`。
3. 右键托盘图标打开菜单，双击打开外观设置；图标可能位于 `^` 隐藏区域。

发布程序未签名，Windows SmartScreen 可能提示未知发布者。

## 界面截图

### 外观设置

调整布局、字体、配色、透明度与 50%–200% 整体缩放，支持实时预览。额度面板可选自动、全部显示或仅周用量；预览使用示例数据。

![外观设置](docs/appearance-settings-reset-credits-zh-cn.png)

### 悬浮条布局

单行布局：

![单行布局](docs/overlay-oneline-credits-zh-cn.png)

多行布局：

![多行布局](docs/overlay-multirow-credits-zh-cn.png)

重置券临近到期时变色提醒：7 天内显示警告色，3 天内显示危险色；无券时自动隐藏。

### 托盘摘要

鼠标悬停托盘图标，查看额度、更新时间与重置券详情。

![托盘摘要](docs/tray-tooltip-zh-cn.png)

## 刷新机制

| 桌面端状态 | 默认间隔 |
|---|---|
| 前台 | 30 秒 |
| 后台可见 | 60 秒 |
| 最小化、隐藏或未运行 | 300 秒 |
| 重置券查询 | 30 分钟 |

程序复用常驻 app-server 连接，并以额度通知补充轮询。切回前台、恢复窗口或系统唤醒时补刷；读取失败保留缓存并退避重试，登录失效清除旧额度。

数据可能与官方界面短暂不同，app-server 总流量也包含自身的后台请求。详见[刷新与诊断说明](docs/usage-refresh.md)。

## 配置

常用选项可在外观设置中调整，其他字段见[默认配置](https://github.com/D1NOOO/codex-usage-monitor/blob/main/config/settings.default.json)。

| 字段 | 可选值 / 默认值 |
|---|---|
| `Language` | `auto` / `zh-CN` / `zh-TW` / `en` |
| `OverlayMode` | `desktop`（默认）/ `attach` |
| `UsageDisplay` | `remaining`（默认）/ `used` |
| `UsagePanels` | `auto`（默认）/ `all` / `weekly` |
| `DisplayLines` | `1` / `2`；右下角吸附默认 `2`，允许手动覆盖 |
| `ShowResetCredits` | 默认 `true` |
| `ForegroundRefreshSeconds` | 30 至 `RefreshSeconds` 秒，默认 30 |
| `RefreshSeconds` | 30–900 秒，默认 60 |
| `MinimizedRefreshSeconds` | 60–3600 秒，默认 300 |
| `ResetCreditsSeconds` | 300–86400 秒，默认 1800 |
| `DiagnosticsEnabled` | 默认 `false` |

整体缩放范围为 50%–200%，叠加 Windows 显示缩放。更新保留 `settings.json`；旧缩放配置自动换算一次，保持实际显示大小。

## 数据与隐私

额度通过本地 `codex app-server` 读取，登录与令牌刷新由 ChatGPT/Codex 管理。重置券查询单独读取本地 `~/.codex/auth.json` 中的访问令牌，仅用于只读接口 `chatgpt.com/backend-api/wham/rate-limit-reset-credits`；令牌仅在请求期间存于内存，不由监视器保存。

诊断默认关闭。启用后，脱敏日志写入 `%LOCALAPPDATA%\CodexRateMonitor\logs`，默认保留 7 天（可设为 1–30 天），总容量上限 20 MiB。安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。

## 常见问题

| 问题 | 处理方式 |
|---|---|
| 找不到 Codex 可执行文件 | 打开或更新 ChatGPT 桌面端，或用 `codex --version` 检查 CLI 安装。 |
| 额度为空 / 登录失效 | 使用 ChatGPT 账号重新登录，再点击托盘菜单的「立即刷新」。 |
| 悬浮条未显示或位置不正确 | 确认监视器正在运行、吸附目标窗口可见，并在外观设置中调整位置。 |

仍有问题时，请[提交 issue](https://github.com/D1NOOO/codex-usage-monitor/issues) 并附上隐藏隐私信息的截图。

## 构建与发布

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\verify.ps1
.\scripts\build.ps1 -Package
```

输出位于 `artifacts/`。发布时更新 `version.txt`，准备中文在前、英文在后的精简说明，经审阅后推送对应 tag。Release 工作流自动构建、测试并发布带来源证明的安装包。

## 许可证

[MIT](LICENSE)。
