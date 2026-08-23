# OneBox

> 一个 Windows 桌面悬浮工具箱：电源计划、音频控制、内存清理、翻译、快捷启动、剪贴板历史，集成进一个可折叠的悬浮窗 + 系统托盘。

紫影主题、圆角卡片、深色 UI，常驻系统托盘，鼠标就近操作。采用 .NET 10 桌面运行时与完整目录发布。

<!-- 截图占位：把悬浮窗截图放到 docs/ 下并取消注释
![OneBox 悬浮窗](docs/screenshot.png)
-->

## 功能

### 悬浮窗
- **紫影主题**：圆角卡片，深色界面，可自定义系统字体
- **固定位置**：拖到哪固定到哪，切换分辨率 / 缩放 / 拔显示器后位置保持不动（仅在窗口完全离开屏幕时才自动回到可视区）
- **锁定位置**：可锁定防止误拖（与"固定位置"独立）
- **窗口置顶**：托盘菜单切换
- **自动折叠**：鼠标离开后按延时自动收起为标题栏，鼠标悬停自动展开（延时可在设置里调，0 = 立即）
- **手动折叠**：点击折叠按钮向上收起
- **鼠标滚轮**：在悬浮窗上滚动直接调音量
- **悬浮提示**：折叠状态下悬停标题栏显示电源 / 音频 / 内存 / 缓存概览

### 电源计划
一键切换 Windows 电源方案（平衡 / 高性能 / 节能 / 自定义），双击按钮打开电源设置。支持非中文系统（按系统 OEM 编码解析 powercfg 输出）。

### 音频控制
- 切换默认输出设备（音箱 / 耳机 / 蓝牙等）
- 音量滑块、静音、实时音量显示
- 支持设备热插拔，插拔后自动刷新
- 每个设备可绑定全局快捷键
- 隐藏不常用的设备

### 内存清理
- **可选清理项**（参考 [memreduct](https://github.com/henrypp/memreduct) 的 NT Native API 实现）：
  - Working set（进程工作集，非管理员也能清理自己的进程）
  - System file cache（系统文件缓存）
  - Standby list*（清空整个备用列表，需管理员，可能短暂卡顿）
  - Modified page list*（刷盘脏页，需管理员，可能短暂卡顿）
  - Standby list (without priority)（仅低优先级备用页）
  - Modified file cache（已修改文件缓存）
  - Registry cache（注册表缓存，Win8.1+）
  - Combine memory lists（合并内存页，Win10+）
- \* 标记项启用时弹确认框，避免误开导致卡顿
- **自动清理**：按时间周期 / 按内存占用率触发；默认跳过两个危险项，可在设置里开启"允许自动清理危险项"
- 实时显示物理内存占用与系统缓存大小
- 托盘固定使用原创 `app.ico`，内存负载与当前状态通过托盘提示文字展示

### 翻译
- 百度大模型翻译 API，独立窗口
- 语言自动检测，支持中 / 英 / 日 / 韩 / 法 / 德 / 俄 / 西 / 阿
- 长文本自动分块，避免 API 长度限制
- 全局快捷键 `Ctrl+Shift+T` 一键翻译剪贴板内容
- **图片翻译**：自定义快捷键框选屏幕区域，调用百度图片翻译 API，返回擦除原文、贴合译文的整图（可复制译文）
- 翻译指令可自定义（意译 / 商务语气 / 保留术语等）
- API Key 用 DPAPI 加密存储

### 截图
- 全局快捷键截取前台窗口客户区，按应用名自动建子目录归档
- Steam 风格右下角完成弹窗，独立图库窗口查看最近截图
- **高级 HDR 截图**（默认关闭）：HDR 显示器 / 全屏游戏回退 Game Bar（Vortice.DXGI 检测 HDR，Game Bar 读取位置与快捷键可配置，绕开"游戏前台吞 Win 键"）

### 性能监控
- **温度 / 风扇**：实时显示 CPU、GPU、主板、内存、硬盘等温度与风扇转速（传感器自选；管理员权限经后台服务 helper 经命名管道提供，无 UAC）
- **性能趋势图表**：悬浮窗入口 + 双击大图；双 Y 轴（温度 + 风扇）；鼠标 tooltip（十字线 + 各线值 + 该时间点前台应用）；前台应用时间段色块标注；时长档 5 分 / 15 分 / 30 分 / 1 时 / 2 时 / 6 时 / 12 时 / 全天，默认 15 分
- **缺口断线**：传感器失配或跨重启的无数据区间显示为断口，而非用上一次的值填满（仅存真实读数，按时间戳对齐）
- 历史持久化 JSON，跨重启保留全天数据；**后台持续采集**（无需打开图表即记录，每 60 秒自动落盘 + 退出落盘，损坏文件自动 .bak 备份），打开图表自动锚到最后一条历史并显示以前记录的全部数据


### 快捷启动栏
8 格启动栏，点击空槽位选择程序（`.exe` / `.lnk`，自动解析快捷方式目标并提取图标）；点击图标启动；右键清空；支持拖拽放入。

### 剪贴板历史
- 记录最近 20 条复制内容（文本 + 图片），文本按内容去重、图片按 SHA256 去重
- DPAPI 加密持久化到磁盘，重启后保留
- 点击悬浮窗按钮弹出历史列表，点击即复制回剪贴板

### 设置
统一设置窗口，标签页：
- **常规**：界面字体（下拉选系统已安装字体，实时预览）、窗口置顶、锁定位置、自动折叠开关与延时、开机自启（注册表 / 计划任务 / 服务三种方式，统一状态标志）
- **板块**：显示 / 隐藏 各功能模块
- **内存**：清理项勾选、自动清理触发条件、危险项确认
- **翻译**：百度翻译 API Key / APPID / 翻译指令
- **截图**：保存位置、截图快捷键、Game Bar 回退配置
- **剪贴板**：历史条数等
- **性能**：传感器选择、采样间隔、告警阈值

### 自动更新
- 启动时后台静默检查 GitHub Release
- 托盘菜单"检查更新"手动触发
- 发现新版本弹窗显示更新内容，使用 Velopack 下载并校验完整版本目录后升级
- 仅正式安装环境执行应用内更新；开发目录和便携目录显示明确提示

## 快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+Shift+T` | 翻译剪贴板内容 |
| 截图快捷键（可自定义） | 截取前台窗口客户区 |
| 图片翻译快捷键（可自定义） | 框选屏幕区域翻译 |
| `Ctrl+Shift+数字`（可自定义） | 切换到指定音频设备 |
| 鼠标滚轮（悬浮窗上） | 调节音量 |

音频设备快捷键在设备项上右键设置，支持冲突检测与覆盖。

## 托盘操作

| 操作 | 功能 |
|------|------|
| 左键单击托盘图标 | 显示悬浮窗 |
| 右键单击托盘图标 | 打开菜单 |
| 中键单击托盘图标 | 立即清理内存 |

## 下载使用

1. 前往 [Releases](../../releases) 下载最新的 Velopack `OneBox-win-Setup.exe`
2. 运行安装器；自动更新仅支持此正式安装环境
3. 开机自启可在 设置 → 常规 里开启

> 需要完整内存清理功能（Standby list 等）时，用托盘菜单或设置里的"以管理员身份重启"。

## 构建

需要 Windows + [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```
dotnet restore OneBox.sln
dotnet build OneBox.sln -c Debug
dotnet build OneBox.sln -c Release
dotnet test OneBox.sln -c Release

# PowerShell：先为 RID 还原，再把 GUI / Service / Hardware 发布到同一目录
dotnet restore OneBox.sln -r win-x64
$publishDir = "artifacts/publish/win-x64"
dotnet publish src/OneBox.csproj -c Release -r win-x64 --self-contained false --no-restore -o $publishDir
dotnet publish src/OneBox.Service/OneBox.Service.csproj -c Release -r win-x64 --self-contained false --no-restore -o $publishDir
dotnet publish src/OneBox.Hardware/OneBox.Hardware.csproj -c Release -r win-x64 --self-contained false --no-restore -o $publishDir

# 可复现打包：还原固定为 1.2.0 的 vpk，将三个项目发布到同一 staging 目录后生成 Velopack 包
powershell -ExecutionPolicy Bypass -File scripts/package.ps1

# 清理编译、测试、打包和发布产物
powershell -ExecutionPolicy Bypass -File scripts/clean.ps1
```

测试使用 xUnit v3 的 VSTest 适配器（`xunit.runner.visualstudio` + `Microsoft.NET.Test.Sdk`）；直接执行上面的 `dotnet test OneBox.sln -c Release` 即可发现并运行测试。

开发构建输出 `src\bin\Debug\net10.0-windows10.0.19041.0\`；普通 `dotnet build OneBox.sln -c Debug/Release` 会按项目依赖先构建 Service、Hardware，再把两个进程及其 `.deps.json`、`runtimeconfig.json`、Contracts 和依赖 DLL 组合到 GUI 同一目录，因此可直接启动该目录的 `OneBox.exe`。完整发布 staging 在 `artifacts\publish\win-x64\`，必须同时包含 `OneBox.exe`、`OneBox.Service.exe`、`OneBox.Hardware.exe`、Contracts 及依赖 DLL，不能只复制单个 exe。`scripts/package.ps1` 从 `Directory.Build.props` 的唯一 `Version` 属性读取版本，生成物写到 `artifacts\packages\win-x64\`。目录 publish 运行需安装 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0)，Velopack Setup 会声明 `net10-x64-desktop` 运行时要求。旧版注册为 `OneBox.exe --service` 的 `OneBoxSvc` 会明确迁移到当前安装目录的 `OneBox.Service.exe`，不会创建并存服务。

## 项目结构

```
OneBox/
├── src/
│   ├── App.cs                  # 入口、单实例、全局异常、编码注册、AppLog
│   ├── MainWindow.cs / MainWindow.*.cs  # 悬浮窗主界面（partial：UI/Data/Hotkeys/Memory/Monitor/Translate/Collapse）
│   ├── AppResources.cs         # 系统字体 + 嵌入资源 + 共享深色样式
│   ├── MaterialTheme.cs / ThemeTokens.cs / IconCatalog.cs  # 本地主题令牌、矢量图标与兼容入口
│   ├── UiKit.cs                # 公共样式与控件（紫影主题复用）
│   ├── LauncherBar.cs / LauncherWindow.cs / LauncherHost.cs  # 快捷启动栏（拖拽 / .lnk / UIPI 嵌入）
│   ├── WindowScaling.cs        # 分辨率缩放 + 固定位置
│   ├── TrayController.cs       # 系统托盘图标与菜单
│   ├── SettingsDialog.cs / SettingsDialog.*.cs  # 统一设置窗口（partial：General/Modules/Memory/Translate/Screenshot/Clipboard/Metrics/Temp）
│   ├── Dialogs.cs              # 翻译窗口、快捷键捕获、统一窗口样式
│   ├── AudioDevices.cs         # 音频设备枚举 / 切换 / 热插拔监听
│   ├── VolumeControl.cs        # 音量控制
│   ├── PowerPlanService.cs     # 电源计划
│   ├── MemoryCleaner.cs        # 内存清理（NT Native API）
│   ├── TranslateService.cs     # 百度文本翻译 API
│   ├── ImageTranslateService.cs / RegionCaptureService.cs / ImageTranslateWindow.cs  # 图片翻译
│   ├── ScreenshotService.cs    # 前台截图 + HDR/Game Bar 回退
│   ├── ScreenshotToast.cs      # 截图 Toast
│   ├── ClipboardHistory.cs     # 剪贴板历史（DPAPI 加密）
│   ├── HardwareMonitorService.cs  # GUI 硬件数据 facade（始终使用 SID 隔离 IPC）
│   ├── PerfHistory.cs / PerfChart.cs / PerfChartWindow.cs  # 性能趋势图表（后台持续采集 + 时间戳断线）
│   ├── ForegroundWatcher.cs / ForegroundHistory.cs  # 前台 exe 捕获 / 前台切换历史（图表标注用）
│   ├── AutoStartService.cs     # 开机自启（服务 / 计划任务 / 注册表）
│   ├── UpdateChecker.cs / UpdateWorkflow.cs  # Velopack 检查、下载、校验、服务协调与应用
│   ├── Native.cs / Prefs.cs / AdminUtils.cs / Models.cs  # Win32 / 注册表 / 提权 / 数据模型
│   ├── OneBox.csproj           # .NET 10 项目文件
│   ├── OneBox.Contracts/       # 版本化 IPC DTO、帧协议、限长、管道名和重连策略
│   ├── OneBox.Service/         # 独立 Windows Service：会话启动、内存清理、helper 守护
│   ├── OneBox.Hardware/        # 独立硬件采集 helper（LibreHardwareMonitor）
│   ├── Shared/                 # Service/Hardware 共用的 Windows 安全管道基础设施
│   ├── app.manifest            # UAC / 兼容性（DPI 由 csproj 配）
│   └── app.ico / app.png       # 应用固定原创图标资源
├── .config/dotnet-tools.json  # 固定 Velopack vpk 1.2.0
├── scripts/package.ps1        # 三程序目录发布 + Velopack 可复现打包
├── Directory.Build.props      # .NET/C# 公共属性与唯一应用版本
├── .gitignore
├── CHANGELOG.md
├── LICENSE
└── README.md
```

## 配置与数据

| 内容 | 位置 |
|------|------|
| 应用设置 | 注册表 `HKCU\Software\PowerAudioManager\App` |
| 设备热键 / 隐藏 | 注册表 `HKCU\Software\PowerAudioManager\Devices` |
| 翻译 API Key | 注册表，DPAPI（CurrentUser）加密 |
| 剪贴板历史 | `%LocalAppData%\OneBox\`（DPAPI 加密） |
| 崩溃日志 | `%TEMP%\pam_crash.log` |
| 运行日志 | exe 同目录 `OneBox.log`（截图/清理/热键/音频/电源/更新等） |
| 服务 / 硬件日志 | 发布目录 `OneBox.Service.log` / `OneBox.Hardware.log` |

## Velopack 自动更新

更新源固定为 `https://github.com/OneT1er/OneBox`，使用 Velopack 1.2.0 的 `GithubSource`。发布新版只修改 `Directory.Build.props` 中唯一的 `<Version>`，运行 `scripts/package.ps1`，再把 `artifacts\packages\win-x64\` 中的 Setup、Portable、full/delta nupkg、`assets.win.json`、`releases.win.json` 与 `RELEASES` 整体上传到对应 GitHub Release。禁止只上传或替换 `OneBox.exe`。

应用内更新由 Velopack 负责 Check、Download、完整版本目录校验、停止 `OneBoxSvc`（其守护的 Hardware helper 会同步退出）和 Apply；重启后的 Velopack hook 会迁移服务路径并确认重新进入 Running。下载、校验、锁冲突、取消、离线和服务协调错误均返回统一中文错误。直接从 `bin`、目录 publish 或 portable zip 运行时会明确提示“当前为开发/便携环境，不能自动更新”，不会尝试原地替换文件或只替换单个 exe。

## 开发说明

- **.NET 10 + WPF**：用 `dotnet build` 构建，现代 C# 语法。主界面、设置和对话框使用 WPF，系统托盘使用 `H.NotifyIcon.Wpf 2.4.1`，托盘图标固定使用 `src/app.ico` 的原创图标资源。
- NuGet 依赖：`Vortice.DXGI`（HDR 检测）、`LibreHardwareMonitorLib`（仅 Hardware helper）、`H.NotifyIcon.Wpf 2.4.1`（WPF 托盘）、`NAudio 3.0.0`（音频枚举、音量和通知）、`Velopack`（安装与目录级更新）。主题与图标由本地 `ThemeTokens` / `IconCatalog` 统一提供；GBK 编码通过 .NET 10 API 注册，包版本统一由根目录 `Directory.Packages.props` 管理。
- 音频枚举、默认设备读取、音量和通知全部通过 NAudio 3.0.0；Windows 没有公开的默认设备设置 API，因此只在切换默认设备处保留最小 `IPolicyConfig` COM adapter，并为 Console、Multimedia、Communications 三个角色逐一设置和释放 COM 对象。
- 字体改用系统字体（设置里可选），不再打包字体文件。
- 内存清理使用 NT Native API（`NtSetSystemInformation`），与 memreduct 同源。

## 致谢

- [memreduct](https://github.com/henrypp/memreduct) — 内存清理的 NT API 实现参考
- 百度翻译 API — 翻译服务
- 紫影主题 #8E8CD8

## 许可证

本项目代码采用 [MIT 许可证](LICENSE) 开源。

## 贡献

欢迎提 Issue 反馈 bug 或建议功能，也欢迎 Pull Request。
