<p align="center">
  <img src="assets/logo.png" alt="Codex 用量监视器项目图标" width="180">
</p>

# Codex 用量监视器（Windows）

**简体中文** · [繁體中文](README.zh-TW.md) · [English](README.md)

一个原生 Windows 托盘小工具，在 Codex App 旁显示当前 5 小时窗口和
7 天窗口的剩余或已用百分比及本地重置时间。

> [!IMPORTANT]
> 这是非官方社区项目，与 OpenAI 无隶属、背书或支持关系。
> `codex app-server` 属于本地/实验性协议，未来 Codex 版本可能调整。

![外观设置](docs/appearance-settings-zh-cn.png)

## 功能

- 默认显示 5 小时与 7 天窗口的剩余百分比，也可切换为显示已用。
- 百分比可选择显示整数或保留 1 位小数。
- 进度条长度及警告/危险颜色会随显示模式同步变化。
- 支持左下角头像右侧、顶部标题栏两种位置。
- 原生 GUI EXE，无 CMD、Node 或 PowerShell 包装窗口。
- 悬浮条不抢焦点，鼠标点击会穿透到 Codex。
- 完整外观设置：字体、字号、缩放、透明度、圆角、颜色和浅色/深色预设。
- 支持简体中文、繁體中文、English，可自动跟随系统或手动选择。
- 可从托盘启用/取消开机启动。
- 不直接读取任何 Codex 凭据文件。

## 运行要求

- Windows 10/11
- .NET Framework 4.8
- 已安装 Codex App 和支持以下命令的 Codex CLI：

  ```powershell
  codex app-server
  ```

- Codex 已正常登录并能返回用量窗口数据。

## 安装与使用

1. 在仓库 **Releases** 页面下载
   `CodexRateMonitor-VERSION-windows-x64.zip`。
2. 可使用 `SHA256SUMS.txt` 校验文件。
3. 解压整个目录到固定位置。
4. 双击 `CodexRateMonitor.exe`。

程序没有主窗口，会常驻 Windows 右下角通知区域。新图标可能先被系统收入
`^` 隐藏区域。

当前发布的 EXE 未进行商业代码签名，SmartScreen 可能提示未知发布者。
请只从本仓库 Release 下载，并校验 SHA256 或 GitHub 构建来源证明。

右击托盘图标可刷新、切换位置、打开外观设置、设置开机启动或退出。
双击托盘图标直接打开外观设置。

### 语言

在 **外观设置 → 界面语言** 中选择：

- 自动（跟随系统）
- 简体中文
- 繁體中文
- English

保存后，托盘菜单、状态提示、设置窗口、悬浮条标签和日期格式都会切换。
重新打开外观设置即可看到整个窗口使用新语言。

## 实现原理

```mermaid
flowchart LR
    UI["托盘图标 + 穿透式悬浮条"] --> Client["AppServerClient"]
    Client -->|"stdin/stdout 上逐行 JSON"| Server["codex app-server"]
    Server --> Auth["Codex 自己管理登录凭据"]
    Server --> API["OpenAI 服务"]
```

程序定位 Codex CLI 的原生可执行文件，然后直接启动：

```text
codex.exe app-server --stdio
```

初始化完成后发送：

```json
{"method":"account/rateLimits/read","id":11}
```

响应包含 `usedPercent`、`windowDurationMins`、`resetsAt` 等字段。
程序把 `primary` 渲染为 5 小时窗口，把 `secondary` 渲染为 7 天窗口；
同时合并 `account/rateLimits/updated` 的稀疏更新。

登录、令牌刷新和与 OpenAI 的网络通信全部由 Codex 负责，本工具不实现认证。

## 隐私与安全

本工具会：

- 仅通过重定向标准输入/输出与子进程 `codex app-server` 通信；
- 在内存里暂存当前用量用于显示；
- 在 `settings.json` 保存显示偏好；
- 用户选择开机启动时，在
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 写入一项。

本工具不会：

- 打开、解析、复制、上传或打印 `auth.json`；
- 保存访问令牌、账户标识或历史用量；
- 写应用日志；
- 加入遥测或统计；
- 要求 OpenAI API Key；
- 把用量发给开发者控制的服务器。

提交 Issue 时，请勿上传 `auth.json`、令牌、账户信息或未经脱敏的桌面截图。
安全问题请按 [SECURITY.md](SECURITY.md) 私下报告。
仓库公开前，请维护者逐项检查 [PUBLISHING.md](PUBLISHING.md)。

## 配置

Release 中的 `settings.json` 来自隐私安全的
`config/settings.default.json`。它只包含显示偏好：

| 字段 | 可选值 |
|---|---|
| `Language` | `auto`、`zh-CN`、`zh-TW`、`en` |
| `Position` | `bottom-left`、`top` |
| `UsageDisplay` | `remaining`（默认）、`used` |
| `PercentDecimalPlaces` | `0`（默认）、`1` |
| `RefreshSeconds` | 30–900 |
| `Style.Scale` | 0.75–1.50 |
| `Style.Opacity` | 0.50–1.00 |
| `Style.FontSize` | 10–22 |
| `Style.ResetFontSize` | 9–18 |

颜色使用 `#RRGGBB` 或 `#RRGGBBAA`。个人运行产生的 `settings.json`
已被 `.gitignore` 排除，仓库只提交默认模板。

## 从源码构建

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\build.ps1 -Package
```

构建脚本使用 Windows/Visual Studio 自带的 .NET Framework 4.8 编译器，
不下载 NuGet 依赖。输出位于 `artifacts/`。CI 会先运行
`scripts/verify.ps1`，检查三语言键、JSON/PowerShell 语法、误提交的个人
设置、个人路径和常见凭据格式。

## GitHub Actions 自动发布

1. 更新 `version.txt` 并提交。
2. 创建同版本标签：

   ```powershell
   git tag v2.2.0
   git push origin main
   git push origin v2.2.0
   ```

3. Release 工作流会自动：
   - 检查标签和 `version.txt` 一致；
   - 在 `windows-latest` 上编译；
   - 生成 ZIP 和 `SHA256SUMS.txt`；
   - 生成 GitHub 构建来源证明；
   - 创建 Release 和自动发布说明。

工作流只使用仓库范围的 `GITHUB_TOKEN`，不需要个人 PAT。发布任务仅授予
`contents: write`、`id-token: write`、`attestations: write`。
所有官方 Action 都固定到完整 Commit SHA，并由 Dependabot 每周检查更新。

验证构建来源：

```powershell
gh attestation verify CodexRateMonitor-VERSION-windows-x64.zip `
  --repo D1NOOO/codex-usage-monitor
```

## 常见问题

### 提示找不到 Codex CLI

```powershell
codex --version
codex app-server --help
```

如果第二条命令不可用，请更新/安装 Codex CLI。

### 提示未登录

请在 Codex App 或 CLI 正常登录。本工具不会接触或代管凭据。

### 悬浮条不显示

将 Codex 切到前台；确认托盘图标仍在；重新选择位置并点击“立即刷新”。

## 已知限制

- 仅支持 Windows。
- 依赖 Codex 本地实验性 app-server 协议。
- 位置偏移适配当前 Codex 桌面布局，Codex UI 更新后可能需要调整。
- Release EXE 尚未代码签名。
- 用量窗口的可用性和含义由 Codex/OpenAI 决定。

## 贡献与许可证

参阅 [CONTRIBUTING.md](CONTRIBUTING.md)。所有可见字符串必须同时维护
简中、繁中和英文；严禁提交凭据、个人路径、真实运行设置或隐私截图。

许可证：[MIT](LICENSE)

Codex 和 OpenAI 是其权利人的商标。本项目为非官方项目，不使用 OpenAI
品牌资产。
