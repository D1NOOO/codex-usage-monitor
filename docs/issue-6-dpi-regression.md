# DPI 与外观回归检查

## 缩放基准与旧配置迁移

新的 100% 对应旧版 85% 的实际尺寸，整体缩放范围扩展到 50%–200%。旧配置缺少
`Style.ScaleBasisVersion` 时，按旧版 75%–150% 范围归一化后将缩放除以 0.85；
渲染统一乘以 0.85，因此保留旧尺寸、字体大小、圆角和间距。加载不重写配置，
保存时持久化版本 2，重复更新/加载/保存不叠加迁移。旧配置省略缩放时按原始发布版
默认 100% 处理。设置界面以两位小数显示迁移值；仅修改其他选项时保留精确缩放。

```powershell
.\scripts\test-scale-migration.ps1 -MonitorPath artifacts\CodexRateMonitor\CodexRateMonitor.exe
# Optional: compare with a local pre-rebase build, without using a real account.
.\scripts\test-scale-migration.ps1 -MonitorPath artifacts\CodexRateMonitor\CodexRateMonitor.exe -LegacyMonitorPath artifacts\usage-refresh-observed\CodexRateMonitor.exe
```

独立迁移测试覆盖旧 75% / 82% / 85% / 100% / 135% / 150%、重复保存、更新器保留配置、
位置和外观不变，以及新上下限。提供旧版程序时，另做三种语言、两种行数、重置券开关、
六种 DPI 下的完整位图比较。

自动检查在仓库根目录运行（先构建，或把路径换成测试包）：

```powershell
.\scripts\test-settings-layout.ps1 -MonitorPath 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
.\scripts\test-settings-dpi-transition.ps1 -MonitorPath 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
.\scripts\test-overlay-dpi.ps1 -MonitorPath 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
.\scripts\test-overlay-surface.ps1 -MonitorPath 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
.\scripts\test-auth-recovery-regression.ps1 -MonitorPath 'artifacts\CodexRateMonitor\CodexRateMonitor.exe'
.\scripts\verify.ps1
```

设置检查覆盖 100%、125%、150%、175%、200%，三种语言和两种显示模式，包含文字、控件重叠、滚动、固定操作区、保存与取消，以及更新窗口。新增的设置窗口动态检查发送模拟 `WM_DPICHANGED`，覆盖 200% → 125% 等 48 次切换；检查窗口和控件尺寸、字体、间距与独立新打开的窗口一致，并确认编辑值保留。悬浮框检查覆盖反复切换 DPI、整体缩放、显示行数、重置券、预览尺寸与绘制结果、吸附边距和点击穿透。更新检查确认配置文件随包复制且保留现有设置。

自动检查不会修改 Windows 显示设置。以下项目需要在实际显示器上验证：

圆角检查覆盖 240 组透明图像：不同 DPI、50% / 75% / 100% / 200% 整体缩放、直角 / 默认圆角 / 最大圆角、两种行数与重置券开关。检查四角透明度渐变和预乘像素，防止硬裁切与光晕；另外短暂显示使用示例数据的测试窗口，核对 Windows 实际合成结果，覆盖浅色 / 深色外观和 50% / 97% / 100% 透明度。测试中的窗口异常只记录到控制台，不弹出 .NET 错误对话框。

| 操作 | 预期 |
| --- | --- |
| 分别在 100%、125%、150%、175%、200% 下退出并启动 | 设置文字完整；悬浮框尺寸符合当前缩放 |
| 保持外观设置窗口打开，200% → 125% → 200% → 125%，重复三次，然后重启 | 窗口、字体和按钮实时调整；相同比例下运行中与重启后的尺寸一致；无累积放大或缩小 |
| 两台不同 DPI 的显示器之间拖动悬浮框和设置窗口 | 各自跟随所在显示器；预览按悬浮框所在显示器的尺寸显示 |
| 2880×1800 / 200%，以及 2K / 175%、200% | 所有选项可到达；操作按钮始终可见 |
| 切换行数、重置券，调整整体缩放至 50%、100%、200% | 预览与悬浮框采用相同布局；预览不足时标明缩小比例 |
| 系统 125% 下，将整体缩放从 100% 改为 80%，保存并重启 | 单行显示重置券时从约 661×42 缩至 529×34 像素；字体、间距和圆角一起缩小，保存值保留 |
| 浅色 / 深色悬浮框放在相反明暗的桌面背景上，改变圆角和透明度 | 四角平滑过渡，无硬裁切、白边或黑色光晕；桌面模式仍可拖动，吸附模式仍点击穿透 |
| 悬浮框停放在任务栏上，改变 DPI、调整样式、取消设置 | 保留位置或仅为防止越界而调整；仍置顶且可拖动 |
| 吸附模式，移动目标窗口或切换显示器 | 保持顶部居中 / 右下角吸附；不抢焦点，点击穿透 |

默认整体缩放为新的 100%、单行且显示重置券时，悬浮框在系统 100%、125%、200% 下的物理尺寸分别约为 529×34、661×42、1057×68 像素。首次启动与「恢复默认」使用新 100%，已有配置按版本换算并保持实际尺寸。预览使用示例数据，因此用量、日期与数量可以不同；实际没有可用重置券时会隐藏该卡片。

测试包应完整解压，`CodexRateMonitor.exe.config` 必须与 EXE 位于同一目录。
