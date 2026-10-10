<p align="center">
  <img src="assets/logo.png" alt="Codex 用量監視器專案圖示" width="180">
</p>

# Codex 用量監視器（Windows）

[简体中文](README.zh-CN.md) · **繁體中文** · [English](README.md)

輕量 Windows 通知區域工具，顯示 Codex 額度、重置時間與重置券，支援桌面懸浮和視窗吸附。

> 非官方社群專案，與 OpenAI 無隸屬關係。依賴實驗性的本機 `codex app-server` 協定。

## 主要功能

- **額度一目了然**：顯示 5 小時、7 天額度及剩餘或已用比例，支援自動、全部顯示和僅週用量面板。
- **靈活浮動版面**：桌面模式置頂、可拖曳；視窗吸附模式點擊穿透，支援單行或多行。
- **重置券唯讀查詢**：顯示可用數量與最早到期時間，不執行核銷。
- **日常使用便捷**：即時外觀預覽、三語介面、開機啟動及驗證式原地更新。

## 安裝與使用

需要 Windows 10/11、.NET Framework 4.8，以及使用 ChatGPT 帳戶登入的 ChatGPT 桌面版、Codex App 或 Codex CLI。API Key 登入不提供訂閱額度。

1. 從 [Releases](https://github.com/D1NOOO/codex-usage-monitor/releases) 下載 `CodexRateMonitor-VERSION-windows-x64.zip`，使用 `SHA256SUMS.txt` 驗證。
2. 解壓縮到固定目錄，執行 `CodexRateMonitor.exe`。
3. 右鍵通知區域圖示開啟選單，雙擊開啟外觀設定；圖示可能位於 `^` 隱藏區域。

發佈程式未簽章，Windows SmartScreen 可能提示未知發行者。

## 介面截圖

### 外觀設定

調整版面、字型、配色、透明度與 50%–200% 整體縮放，支援即時預覽。額度面板可選自動、全部顯示或僅週用量；預覽使用示範資料。

![外觀設定](docs/appearance-settings-reset-credits-zh-cn.png)

### 浮動列版面

單行版面：

![單行版面](docs/overlay-oneline-credits-zh-cn.png)

多行版面：

![多行版面](docs/overlay-multirow-credits-zh-cn.png)

重置券接近到期時變色提醒：7 天內顯示警告色，3 天內顯示危險色；無券時自動隱藏。

### 通知區域摘要

滑鼠懸停通知區域圖示，查看額度、更新時間與重置券詳情。

![通知區域摘要](docs/tray-tooltip-zh-cn.png)

## 重新整理機制

| 桌面版狀態 | 預設間隔 |
|---|---|
| 前景 | 30 秒 |
| 背景可見 | 60 秒 |
| 最小化、隱藏或未執行 | 300 秒 |
| 重置券查詢 | 30 分鐘 |

程式重用常駐 app-server 連線，並以額度通知補充輪詢。切回前景、還原視窗或系統喚醒時補刷；讀取失敗保留快取並退避重試，登入失效清除舊額度。

資料可能與官方介面短暫不同，app-server 總流量也包含自身的背景請求。詳見[重新整理與診斷說明](docs/usage-refresh.md)。

## 設定

常用選項可在外觀設定中調整，其他欄位見[預設設定](https://github.com/D1NOOO/codex-usage-monitor/blob/main/config/settings.default.json)。

| 欄位 | 可選值 / 預設值 |
|---|---|
| `Language` | `auto` / `zh-CN` / `zh-TW` / `en` |
| `OverlayMode` | `desktop`（預設）/ `attach` |
| `UsageDisplay` | `remaining`（預設）/ `used` |
| `UsagePanels` | `auto`（預設）/ `all` / `weekly` |
| `DisplayLines` | `1` / `2`；右下角吸附預設 `2`，允許手動覆寫 |
| `ShowResetCredits` | 預設 `true` |
| `ForegroundRefreshSeconds` | 30 至 `RefreshSeconds` 秒，預設 30 |
| `RefreshSeconds` | 30–900 秒，預設 60 |
| `MinimizedRefreshSeconds` | 60–3600 秒，預設 300 |
| `ResetCreditsSeconds` | 300–86400 秒，預設 1800 |
| `DiagnosticsEnabled` | 預設 `false` |

整體縮放範圍為 50%–200%，疊加 Windows 顯示縮放。更新保留 `settings.json`；舊縮放設定自動換算一次，保持實際顯示大小。

## 資料與隱私

額度透過本機 `codex app-server` 讀取，登入與權杖更新由 ChatGPT/Codex 管理。重置券查詢另外讀取本機 `~/.codex/auth.json` 中的存取權杖，僅用於唯讀端點 `chatgpt.com/backend-api/wham/rate-limit-reset-credits`；權杖僅在請求期間存於記憶體，不由監視器儲存。

診斷預設關閉。啟用後，去識別化日誌寫入 `%LOCALAPPDATA%\CodexRateMonitor\logs`，預設保留 7 天（可設為 1–30 天），總容量上限 20 MiB。安全問題請依 [SECURITY.md](SECURITY.md) 私下回報。

## 常見問題

| 問題 | 處理方式 |
|---|---|
| 找不到 Codex 執行檔 | 開啟或更新 ChatGPT 桌面版，或用 `codex --version` 檢查 CLI 安裝。 |
| 額度為空 / 登入失效 | 使用 ChatGPT 帳戶重新登入，再點擊通知區域選單的「立即重新整理」。 |
| 浮動列未顯示或位置不正確 | 確認監視器正在執行、吸附目標視窗可見，並在外觀設定中調整位置。 |

仍有問題時，請[提交 issue](https://github.com/D1NOOO/codex-usage-monitor/issues) 並附上隱藏私人資訊的截圖。

## 建置與發佈

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\verify.ps1
.\scripts\build.ps1 -Package
```

輸出位於 `artifacts/`。發佈時更新 `version.txt`，準備中文在前、英文在後的精簡說明，經審閱後推送對應標籤。Release 工作流程自動建置、測試並發佈帶來源證明的安裝包。

## 授權

[MIT](LICENSE)。
