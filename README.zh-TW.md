<p align="center">
  <img src="assets/logo.png" alt="Codex 用量監視器專案圖示" width="180">
</p>

# Codex 用量監視器（Windows）

[简体中文](README.zh-CN.md) · **繁體中文** · [English](README.md)

一個原生 Windows 通知區域小工具，在 ChatGPT 桌面版的 Codex 介面旁顯示目前 5 小時與
7 天用量視窗，並展示可用的限額重置券。

> [!IMPORTANT]
> 非官方社群專案，與 OpenAI 無隸屬關係。`codex app-server` 是本機實驗性協定，
> 未來版本可能調整。

## 介面截圖

浮動列支援 1 行與多行兩種版面。多行版面中的重置券卡片會依到期時間分級變色：
正常 → 警告（≤7 天）→ 危險染色（≤3 天），無券時自動隱藏。

![多行版面](docs/overlay-multirow-credits-zh-cn.png)

1 行版面把 5 小時、7 天與重置券卡片橫向排成一行，適合嵌入標題列。

![1 行版面](docs/overlay-oneline-credits-zh-cn.png)

滑鼠懸停通知區域圖示可查看分行顯示的用量與重置券摘要。

![通知區域浮動提示](docs/tray-tooltip-zh-cn.png)

外觀設定提供樣式預覽：位置、字型、顏色、透明度，以及「顯示重置券（唯讀查詢）」開關。預覽中的用量、時間和重置券數量為示範資料。
「額度面板」提供「自動（推薦）」「全部顯示（5h+7d）」「僅顯示週用量」三個選項。自動模式下，完整讀取確認帳號只有 7 天額度時，收起 5 小時面板，橫排縮短寬度、多行縮短高度；預覽與系統匣摘要同步調整。「全部顯示（5h+7d）」保留缺少的視窗，標示「未提供」；「僅顯示週用量」固定顯示 7 天額度。首次讀取顯示狀態提示，重新整理失敗保留上次資料與版面，不完整推送不會收起面板。
選擇「視窗吸附 + 右下角」時預設切換為多行，之後仍可手動選擇 1 行；已儲存的行數在重新開啟設定或重新啟動後保留。
浮動列會即時跟隨 Windows 顯示縮放，「整體縮放」在此基礎上疊加。預覽空間足夠時按實際尺寸顯示，空間不足時會標明縮小比例。
整體縮放預設新的 100%（對應原來 85% 的尺寸），可在「外觀設定 → 排版與尺寸 → 整體縮放」調整為 50%–200%。字型、間距與圓角一起縮放，舊設定更新後保持實際大小。

![外觀設定](docs/appearance-settings-reset-credits-zh-cn.png)

## 功能

- 浮動列支援桌面懸浮（置頂、可拖曳、點擊穿透）與視窗吸附兩種模式，
  1 行 / 多行兩種版面，剩餘 / 已用兩種顯示口徑。
- 重置券卡片：唯讀顯示可用數量與最早到期時間，絕不呼叫核銷介面。
- 通知區域浮動提示分行顯示用量與重置券摘要。
- 週期重新整理複用常駐 app-server，視窗最小化時自動降頻（見下文「流量消耗」）。
- 其餘：三語介面（簡中 / 繁中 / 英文）、完整外觀編輯、開機啟動、
  背景更新檢查與校驗式原地更新。

## 執行需求

- Windows 10/11，.NET Framework 4.8。
- 已登入的 ChatGPT 桌面版（舊版 Codex App + Codex CLI 仍相容）。
  API Key 登入無法顯示訂閱用量視窗。

## 安裝

1. 從 [Releases](https://github.com/D1NOOO/codex-usage-monitor/releases) 下載
   `CodexRateMonitor-VERSION-windows-x64.zip`，按需驗證 `SHA256SUMS.txt`。
2. 解壓縮到固定位置，執行 `CodexRateMonitor.exe`。程式沒有主視窗，會常駐通知區域
   （可能先被收進 `^` 隱藏區域）。右鍵圖示設定，雙擊開啟外觀設定。

EXE 未做商業簽章，SmartScreen 可能提示未知發行者；請只從 Releases 下載並驗證。

## 流量消耗

程式專門為低流量設計（背景見
[issue #5](https://github.com/D1NOOO/codex-usage-monitor/issues/5)）：

| 情境 | 行為 |
|---|---|
| ChatGPT/Codex 位於前景 | 每 `ForegroundRefreshSeconds`（預設 30s）一次輕量讀取 |
| 視窗可見但位於背景 | 每 `RefreshSeconds`（預設 60s）一次讀取 |
| 視窗最小化 / 僅通知區域 | 降頻到 `MinimizedRefreshSeconds`（預設 300s） |
| 重置券查詢 | 每 `ResetCreditsSeconds`（預設 30 分鐘）一次唯讀請求 |

app-server 程序全程常駐複用：先以 `account/read`（`refreshToken: false`）確認本機帳號，
再傳送 `account/rateLimits/read`，並接收監視器自身連線的 `account/rateLimits/updated`
通知。切回前景、還原視窗、系統喚醒後等待 1 秒合併補刷；同時觸發的重新整理複用
一個請求，30 秒逾時，失敗後逐步退避到最多 5 分鐘。額度連續相同不會觸發重啟。
浮動列與通知區域提示顯示上次確認更新的時間，帳號變更時清除舊資料。
這能減少輪詢等待，但無法保證與官方介面獨立快取的資料逐秒一致。app-server 也可能自行發出
背景請求，因此程序總流量可能高於用量讀取本身。

如果 app-server 回報權杖失效，浮動列會立即清除舊用量。連續失敗時最多自動重啟
app-server 一次；若登入仍無效，程式停止自動重啟，恢復探測間隔不短於 5 分鐘。
透過 ChatGPT/Codex 重新登入後，可點擊通知區域的「立即重新整理」馬上重試。

## 實作原理

```mermaid
flowchart LR
    UI["通知區域圖示 + 穿透式浮動列"] --> Client["AppServerClient"]
    Client -->|"stdin/stdout 上逐行 JSON"| Server["codex app-server"]
    Server --> Auth["ChatGPT/Codex 自行管理登入憑據"]
    Server --> API["OpenAI 服務"]
```

程式按「桌面版內建 → npm 安裝 → PATH」的順序尋找 Codex 執行檔，啟動
`codex.exe app-server`，傳送 `account/rateLimits/read`，依視窗時長識別 5 小時與 7 天額度，
並合併 `account/rateLimits/updated` 推送。7 天視窗出現在 `primary` 時仍識別為 7 天額度。

登入、權杖更新及與 OpenAI 的通訊全部由 ChatGPT/Codex 負責，本工具不實作驗證。
唯一例外：啟用重置券時，程式本機讀取 `~/.codex/auth.json` 的存取權杖，僅用於查詢唯讀端點
`chatgpt.com/backend-api/wham/rate-limit-reset-credits`——權杖只在單次請求期間存於記憶體，
絕不核銷、不儲存、不外傳。診斷日誌寫入 `%LOCALAPPDATA%\CodexRateMonitor\logs`
並自動清理，不含權杖與帳戶資訊。安全問題請依 [SECURITY.md](SECURITY.md) 私下回報。

診斷預設關閉。排查時將 `settings.json` 中的 `DiagnosticsEnabled` 改為 `true`，
重新啟動監視器後生效。日誌預設保留 7 天，單檔約 2 MiB 時輪轉，總容量預算 20 MiB；
關閉診斷後，只要監視器仍在執行，也會清理過期日誌。重新整理原因、請求耗時、通知處理
及重置時間補刷規則見 [用量重新整理與診斷說明](docs/usage-refresh.md)。

## 設定

`settings.json`（來自 `config/settings.default.json`）常用欄位：

| 欄位 | 說明 |
|---|---|
| `Language` | `auto` / `zh-CN` / `zh-TW` / `en` |
| `OverlayMode` | `desktop`（預設，桌面懸浮）/ `attach`（吸附視窗） |
| `UsageDisplay` | `remaining`（預設）/ `used` |
| `UsagePanels` | `auto`（預設，自動調整可用額度）/ `all`（全部顯示 5h+7d）/ `weekly`（僅顯示週用量） |
| `DisplayLines` | `1` / `2`；選取右下角視窗吸附時預設 `2`，允許手動覆寫 |
| `ForegroundRefreshSeconds` | 30–`RefreshSeconds`，前景重新整理間隔（預設 30） |
| `RefreshSeconds` | 30–900，視窗可見但位於背景時的重新整理間隔（預設 60） |
| `MinimizedRefreshSeconds` | 60–3600，最小化時的重新整理間隔（預設 300） |
| `ShowResetCredits` | 重置券卡片開關（預設開） |
| `ResetCreditsSeconds` | 300–86400，重置券查詢間隔（預設 1800） |
| `DiagnosticsEnabled` / `DiagnosticRetentionDays` | 診斷預設關閉，預設保留 7 天（1–30），總容量預算 20 MiB |

其餘外觀欄位（字型、顏色 `#RRGGBB`、縮放、透明度等）見預設範本或外觀設定介面。

整體縮放範圍為 50%–200%，新的 100% 使用原來 85% 的緊湊尺寸。舊設定在記憶體中換算，
保持實際大小不變：舊 85% 對應新 100%，舊 100% 對應新約 117.65%。更新器保留既有
`settings.json`，儲存設定時寫入 `Style.ScaleBasisVersion: 2`，避免重複換算；
字型、顏色、透明度、位置及其他設定繼續保留。

## 建置與發佈

```powershell
git clone https://github.com/D1NOOO/codex-usage-monitor.git
cd codex-usage-monitor
.\scripts\build.ps1 -Package
```

輸出位於 `artifacts/`。CI 先執行 `scripts/verify.ps1`（三語鍵、JSON/PowerShell 語法、
隱私與憑據掃描）。發佈時更新 `version.txt`、推送同名標籤，
`.github/workflows/release.yml` 自動建置並建立帶來源證明的 Release。

## FAQ

### 找不到 Codex 執行檔 / 未登入

先開啟或更新 ChatGPT 桌面版；獨立 CLI 使用者用 `codex --version` 與
`codex app-server --help` 確認可用，`codex doctor` 排查登入。若為 API Key 登入狀態，
請改用 ChatGPT 帳戶登入——API Key 無法返回訂閱用量視窗。

### 浮動列沒有顯示

把 ChatGPT/Codex 視窗切到前景（吸附模式只在視窗可見時顯示）；確認通知區域圖示在執行；
必要時點擊「立即重新整理」。

### UI 更新後位置偏了

位置偏移配合目前桌面版版面，介面更新後可能需要調整。歡迎提交帶去識別化截圖的 issue。

## 已知限制

僅支援 Windows；依賴本機實驗性 app-server 協定；Release EXE 未做程式碼簽章。

## 授權

[MIT](LICENSE)。Codex 和 OpenAI 是其權利人的商標，本專案為非官方專案。
