using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexRateMonitorNative
{
    internal static class I18n
    {
        private static readonly Dictionary<string, Dictionary<string, string>> Tables =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "zh-CN", new Dictionary<string, string>
                    {
                        {"AppTitle", "Codex 用量监视器"},
                        {"RefreshNow", "立即刷新"},
                        {"CheckUpdates", "检测更新…"},
                        {"CheckingUpdates", "正在检测更新…"},
                        {"UpdateAvailableMenu", "发现新版本 v{0}"},
                        {"UpdateAvailableTray", "Codex 用量监视器：发现新版本 v{0}"},
                        {"UpdateTitle", "软件更新"},
                        {"UpdateAvailableTitle", "发现新版本 v{0}"},
                        {"UpdateVersionLine", "当前版本 v{0}  →  最新版本 v{1}"},
                        {"UpdateWhatsNew", "新版本功能"},
                        {"UpdateAction", "更新"},
                        {"AlreadyLatest", "当前 v{0} 已是最新版本。"},
                        {"UpdateCheckFailed", "暂时无法检测更新，请稍后重试。"},
                        {"UpdateDownloading", "正在下载并校验 Release 包…"},
                        {"UpdateInstalling", "正在退出并安装更新…"},
                        {"UpdateInstallFailed", "更新失败：{0}"},
                        {"UpdateAssetsMissing", "Release 中缺少 Windows 更新包或校验文件。"},
                        {"UpdateChecksumMissing", "校验文件中没有找到更新包。"},
                        {"UpdateChecksumFailed", "更新包 SHA-256 校验失败。"},
                        {"ReloadStyle", "重新加载样式"},
                        {"AppearanceMenu", "外观设置…"},
                        {"TopPosition", "顶部标题栏"},
                        {"BottomPosition", "右下角"},
                        {"TopRecommended", "顶部标题栏（推荐）"},
                        {"Startup", "开机启动"},
                        {"Exit", "退出"},
                        {"Connecting", "连接中"},
                        {"Unavailable", "未提供"},
                        {"ServiceError", "服务错误"},
                        {"StartFailed", "启动失败：{0}"},
                        {"UsageTray", "Codex {0}：5小时 {1} · 7天 {2}"},
                        {"TrayStatus", "Codex 用量监视器：{0}"},
                        {"StyleReloaded", "Codex 用量监视器：样式已重新加载"},
                        {"AppearanceSaved", "Codex 用量监视器：外观已保存"},
                        {"FiveHour", "5小时"},
                        {"SevenDay", "7天"},
                        {"CliMissing", "未找到原生 Codex 可执行文件。请打开或更新 ChatGPT 桌面端，或安装 Codex CLI。"},
                        {"CommunicationError", "通信错误：{0}"},
                        {"InitializationFailed", "初始化失败"},
                        {"NotSignedIn", "未登录"},
                        {"ChatGptAuthRequired", "需要 ChatGPT 账户登录；API Key 登录无法读取 5小时/7天用量"},
                        {"SettingsTitle", "Codex 用量监视器 · 外观设置"},
                        {"DisplayPosition", "显示位置"},
                        {"DisplaySettings", "显示设置"},
                        {"ProgressDisplay", "进度显示"},
                        {"ShowRemaining", "显示剩余（默认）"},
                        {"ShowUsed", "显示已用"},
                        {"Remaining", "剩余"},
                        {"Used", "已用"},
                        {"Typography", "排版与尺寸"},
                        {"Colors", "颜色"},
                        {"SettingsHint", "设置会在上方实时预览。保存后写入 settings.json；取消会恢复打开窗口前的样式。"},
                        {"AppearanceTitle", "外观设置"},
                        {"AppearanceSubtitle", "调整悬浮条的位置、字体、尺寸和配色"},
                        {"Font", "字体"},
                        {"MainFontSize", "主字号"},
                        {"TimeFontSize", "时间字号"},
                        {"Scale", "整体缩放"},
                        {"Opacity", "透明度"},
                        {"CornerRadius", "圆角"},
                        {"OuterBackground", "外框背景"},
                        {"RowBackground", "行背景"},
                        {"Border", "边框"},
                        {"ProgressTrack", "进度槽"},
                        {"MainText", "主要文字"},
                        {"TimeText", "时间文字"},
                        {"FiveHourColor", "5 小时"},
                        {"SevenDayColor", "7 天"},
                        {"Warning", "警告"},
                        {"Danger", "危险"},
                        {"DarkPreset", "深色预设"},
                        {"LightPreset", "浅色预设"},
                        {"SaveClose", "保存并关闭"},
                        {"Cancel", "取消"},
                        {"RestoreDefault", "恢复默认"},
                        {"LivePreview", "实时预览"},
                        {"Language", "界面语言"},
                        {"LanguageAuto", "自动（跟随系统）"}
                    }
                },
                {
                    "zh-TW", new Dictionary<string, string>
                    {
                        {"AppTitle", "Codex 用量監視器"},
                        {"RefreshNow", "立即重新整理"},
                        {"CheckUpdates", "檢查更新…"},
                        {"CheckingUpdates", "正在檢查更新…"},
                        {"UpdateAvailableMenu", "發現新版本 v{0}"},
                        {"UpdateAvailableTray", "Codex 用量監視器：發現新版本 v{0}"},
                        {"UpdateTitle", "軟體更新"},
                        {"UpdateAvailableTitle", "發現新版本 v{0}"},
                        {"UpdateVersionLine", "目前版本 v{0}  →  最新版本 v{1}"},
                        {"UpdateWhatsNew", "新版本功能"},
                        {"UpdateAction", "更新"},
                        {"AlreadyLatest", "目前 v{0} 已是最新版本。"},
                        {"UpdateCheckFailed", "暫時無法檢查更新，請稍後再試。"},
                        {"UpdateDownloading", "正在下載並驗證 Release 套件…"},
                        {"UpdateInstalling", "正在結束並安裝更新…"},
                        {"UpdateInstallFailed", "更新失敗：{0}"},
                        {"UpdateAssetsMissing", "Release 中缺少 Windows 更新套件或驗證檔案。"},
                        {"UpdateChecksumMissing", "驗證檔案中找不到更新套件。"},
                        {"UpdateChecksumFailed", "更新套件 SHA-256 驗證失敗。"},
                        {"ReloadStyle", "重新載入樣式"},
                        {"AppearanceMenu", "外觀設定…"},
                        {"TopPosition", "頂部標題列"},
                        {"BottomPosition", "右下角"},
                        {"TopRecommended", "頂部標題列（建議）"},
                        {"Startup", "開機啟動"},
                        {"Exit", "結束"},
                        {"Connecting", "連線中"},
                        {"Unavailable", "未提供"},
                        {"ServiceError", "服務錯誤"},
                        {"StartFailed", "啟動失敗：{0}"},
                        {"UsageTray", "Codex {0}：5小時 {1} · 7天 {2}"},
                        {"TrayStatus", "Codex 用量監視器：{0}"},
                        {"StyleReloaded", "Codex 用量監視器：樣式已重新載入"},
                        {"AppearanceSaved", "Codex 用量監視器：外觀已儲存"},
                        {"FiveHour", "5小時"},
                        {"SevenDay", "7天"},
                        {"CliMissing", "找不到原生 Codex 可執行檔。請開啟或更新 ChatGPT 桌面版，或安裝 Codex CLI。"},
                        {"CommunicationError", "通訊錯誤：{0}"},
                        {"InitializationFailed", "初始化失敗"},
                        {"NotSignedIn", "尚未登入"},
                        {"ChatGptAuthRequired", "需要 ChatGPT 帳戶登入；API Key 登入無法讀取 5小時/7天用量"},
                        {"SettingsTitle", "Codex 用量監視器 · 外觀設定"},
                        {"DisplayPosition", "顯示位置"},
                        {"DisplaySettings", "顯示設定"},
                        {"ProgressDisplay", "進度顯示"},
                        {"ShowRemaining", "顯示剩餘（預設）"},
                        {"ShowUsed", "顯示已用"},
                        {"Remaining", "剩餘"},
                        {"Used", "已用"},
                        {"Typography", "排版與尺寸"},
                        {"Colors", "顏色"},
                        {"SettingsHint", "設定會在上方即時預覽。儲存後寫入 settings.json；取消會還原開啟視窗前的樣式。"},
                        {"AppearanceTitle", "外觀設定"},
                        {"AppearanceSubtitle", "調整浮動列的位置、字型、尺寸與配色"},
                        {"Font", "字型"},
                        {"MainFontSize", "主要字級"},
                        {"TimeFontSize", "時間字級"},
                        {"Scale", "整體縮放"},
                        {"Opacity", "透明度"},
                        {"CornerRadius", "圓角"},
                        {"OuterBackground", "外框背景"},
                        {"RowBackground", "列背景"},
                        {"Border", "邊框"},
                        {"ProgressTrack", "進度槽"},
                        {"MainText", "主要文字"},
                        {"TimeText", "時間文字"},
                        {"FiveHourColor", "5 小時"},
                        {"SevenDayColor", "7 天"},
                        {"Warning", "警告"},
                        {"Danger", "危險"},
                        {"DarkPreset", "深色預設"},
                        {"LightPreset", "淺色預設"},
                        {"SaveClose", "儲存並關閉"},
                        {"Cancel", "取消"},
                        {"RestoreDefault", "還原預設"},
                        {"LivePreview", "即時預覽"},
                        {"Language", "介面語言"},
                        {"LanguageAuto", "自動（跟隨系統）"}
                    }
                },
                {
                    "en", new Dictionary<string, string>
                    {
                        {"AppTitle", "Codex Rate Monitor"},
                        {"RefreshNow", "Refresh now"},
                        {"CheckUpdates", "Check for updates…"},
                        {"CheckingUpdates", "Checking for updates…"},
                        {"UpdateAvailableMenu", "Update v{0} available"},
                        {"UpdateAvailableTray", "Codex Rate Monitor: update v{0} available"},
                        {"UpdateTitle", "Software update"},
                        {"UpdateAvailableTitle", "Update v{0} is available"},
                        {"UpdateVersionLine", "Current v{0}  →  Latest v{1}"},
                        {"UpdateWhatsNew", "What's new"},
                        {"UpdateAction", "Update"},
                        {"AlreadyLatest", "Version v{0} is already the latest version."},
                        {"UpdateCheckFailed", "Unable to check for updates. Please try again later."},
                        {"UpdateDownloading", "Downloading and verifying the Release package…"},
                        {"UpdateInstalling", "Exiting and installing the update…"},
                        {"UpdateInstallFailed", "Update failed: {0}"},
                        {"UpdateAssetsMissing", "The Release is missing its Windows package or checksum file."},
                        {"UpdateChecksumMissing", "The update package is not listed in the checksum file."},
                        {"UpdateChecksumFailed", "The update package SHA-256 checksum does not match."},
                        {"ReloadStyle", "Reload style"},
                        {"AppearanceMenu", "Appearance settings…"},
                        {"TopPosition", "Top title bar"},
                        {"BottomPosition", "Bottom-right"},
                        {"TopRecommended", "Top title bar (recommended)"},
                        {"Startup", "Start with Windows"},
                        {"Exit", "Exit"},
                        {"Connecting", "Connecting"},
                        {"Unavailable", "Not provided"},
                        {"ServiceError", "Service error"},
                        {"StartFailed", "Start failed: {0}"},
                        {"UsageTray", "Codex {0}: 5h {1} · 7d {2}"},
                        {"TrayStatus", "Codex Rate Monitor: {0}"},
                        {"StyleReloaded", "Codex Rate Monitor: style reloaded"},
                        {"AppearanceSaved", "Codex Rate Monitor: appearance saved"},
                        {"FiveHour", "5h"},
                        {"SevenDay", "7d"},
                        {"CliMissing", "Native Codex executable not found. Open or update ChatGPT desktop, or install Codex CLI."},
                        {"CommunicationError", "Communication error: {0}"},
                        {"InitializationFailed", "Initialization failed"},
                        {"NotSignedIn", "Not signed in"},
                        {"ChatGptAuthRequired", "ChatGPT account sign-in required; API key auth cannot read 5h/7d usage"},
                        {"SettingsTitle", "Codex Rate Monitor · Appearance"},
                        {"DisplayPosition", "Display position"},
                        {"DisplaySettings", "Display settings"},
                        {"ProgressDisplay", "Progress display"},
                        {"ShowRemaining", "Show remaining (default)"},
                        {"ShowUsed", "Show used"},
                        {"Remaining", "remaining"},
                        {"Used", "used"},
                        {"Typography", "Typography and size"},
                        {"Colors", "Colors"},
                        {"SettingsHint", "Changes are previewed above. Save writes settings.json; Cancel restores the previous appearance."},
                        {"AppearanceTitle", "Appearance"},
                        {"AppearanceSubtitle", "Adjust position, fonts, sizing, and colors"},
                        {"Font", "Font"},
                        {"MainFontSize", "Main size"},
                        {"TimeFontSize", "Time size"},
                        {"Scale", "Scale"},
                        {"Opacity", "Opacity"},
                        {"CornerRadius", "Corner radius"},
                        {"OuterBackground", "Outer bg"},
                        {"RowBackground", "Row bg"},
                        {"Border", "Border"},
                        {"ProgressTrack", "Track"},
                        {"MainText", "Main text"},
                        {"TimeText", "Time text"},
                        {"FiveHourColor", "5-hour"},
                        {"SevenDayColor", "7-day"},
                        {"Warning", "Warning"},
                        {"Danger", "Danger"},
                        {"DarkPreset", "Dark preset"},
                        {"LightPreset", "Light preset"},
                        {"SaveClose", "Save and close"},
                        {"Cancel", "Cancel"},
                        {"RestoreDefault", "Restore defaults"},
                        {"LivePreview", "Live preview"},
                        {"Language", "Language"},
                        {"LanguageAuto", "Auto (system)"}
                    }
                }
            };

        private static string currentLanguage = "en";

        public static string CurrentLanguage
        {
            get { return currentLanguage; }
        }

        public static void SetLanguage(string setting)
        {
            currentLanguage = ResolveLanguage(setting);
        }

        public static string NormalizeSetting(string setting)
        {
            if (string.Equals(setting, "zh-CN", StringComparison.OrdinalIgnoreCase))
                return "zh-CN";
            if (string.Equals(setting, "zh-TW", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase))
                return "en";
            return "auto";
        }

        public static string ResolveLanguage(string setting)
        {
            string normalized = NormalizeSetting(setting);
            if (normalized != "auto")
                return normalized;
            string name = CultureInfo.CurrentUICulture.Name;
            if (name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zh-MO", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
                return "zh-TW";
            if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return "zh-CN";
            return "en";
        }

        public static string T(string key)
        {
            return Translate(key, currentLanguage);
        }

        public static string Translate(string key, string languageSetting)
        {
            string language = ResolveLanguage(languageSetting);
            Dictionary<string, string> table;
            string value;
            if (Tables.TryGetValue(language, out table) && table.TryGetValue(key, out value))
                return value;
            if (Tables["en"].TryGetValue(key, out value))
                return value;
            return key;
        }

        public static string F(string key, params object[] args)
        {
            return string.Format(CultureInfo.CurrentCulture, T(key), args);
        }

        public static string FormatDate(DateTime value)
        {
            return FormatDate(value, currentLanguage);
        }

        public static string FormatDate(DateTime value, string languageSetting)
        {
            string language = ResolveLanguage(languageSetting);
            if (language == "en")
                return value.ToString("MMM d HH:mm", CultureInfo.GetCultureInfo("en-US"));
            return value.ToString("M.d HH:mm", CultureInfo.InvariantCulture);
        }
    }

    internal sealed class LanguageOption
    {
        public string Code { get; private set; }
        public string Label { get; private set; }

        public LanguageOption(string code, string label)
        {
            Code = code;
            Label = label;
        }

        public override string ToString()
        {
            return Label;
        }
    }
}
