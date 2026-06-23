# ⚡ OneBox

> 一个 Windows 桌面悬浮工具箱：电源计划、音频控制、内存清理、翻译、快捷启动、剪贴板历史，集成进一个可折叠的悬浮窗 + 系统托盘。

紫影主题、圆角卡片、深色 UI，常驻任务栏，鼠标就近操作。单文件 exe，无需安装运行时。

<!-- 截图占位：把悬浮窗截图放到 docs/ 下并取消注释
![OneBox 悬浮窗](docs/screenshot.png)
-->

## ✨ 功能

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
- 动态托盘图标按内存负载变色（绿 < 60% / 黄 60–80% / 红 > 80%）

### 翻译
- 百度大模型翻译 API，独立窗口
- 语言自动检测，支持中 / 英 / 日 / 韩 / 法 / 德 / 俄 / 西 / 阿
- 长文本自动分块，避免 API 长度限制
- 全局快捷键 `Ctrl+Shift+T` 一键翻译剪贴板内容
- 翻译指令可自定义（意译 / 商务语气 / 保留术语等）
- API Key 用 DPAPI 加密存储

### 快捷启动栏
4 格启动栏，点击空格选择程序（`.exe` / `.lnk`，自动解析快捷方式目标并提取图标）；点击图标启动；右键清空；支持拖拽放入。

### 剪贴板历史
- 记录最近 20 条复制内容（文本 + 图片），文本按内容去重、图片按 SHA256 去重
- DPAPI 加密持久化到磁盘，重启后保留
- 点击悬浮窗按钮弹出历史列表，点击即复制回剪贴板

### 设置
统一设置窗口，四个标签页：
- **常规**：界面字体（下拉选系统已安装字体，实时预览）、窗口置顶、锁定位置、自动折叠开关与延时、开机自启
- **板块**：显示 / 隐藏 电源 / 音频 / 内存 / 翻译 / 启动栏 / 剪贴板
- **内存**：清理项勾选、自动清理触发条件、危险项确认
- **翻译**：百度翻译 API Key / APPID / 翻译指令

### 自动更新
- 启动时后台静默检查 GitHub Release
- 托盘菜单"检查更新"手动触发
- 发现新版本弹窗显示更新内容，支持应用内下载并替换升级
- 更新临时文件使用随机名，避免本地文件抢占

## ⌨️ 快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+Shift+T` | 翻译剪贴板内容 |
| `Ctrl+Shift+数字`（可自定义） | 切换到指定音频设备 |
| 鼠标滚轮（悬浮窗上） | 调节音量 |

音频设备快捷键在设备项上右键设置，支持冲突检测与覆盖。

## 🖱️ 托盘操作

| 操作 | 功能 |
|------|------|
| 左键单击托盘图标 | 显示 / 隐藏悬浮窗 |
| 右键单击托盘图标 | 打开菜单 |
| 中键单击托盘图标 | 立即清理内存 |

## 🚀 下载使用

1. 前往 [Releases](../../releases) 下载最新的 `OneBox.exe`
2. 双击运行，无需安装
3. 开机自启可在 设置 → 常规 里开启

> 需要完整内存清理功能（Standby list 等）时，用托盘菜单或设置里的"以管理员身份重启"。

## 🔧 构建

需要 Windows + .NET Framework 4.x（系统自带 `csc.exe`，无需额外安装）。

```
cd src
build.bat
```

输出在 `src\output\OneBox.exe`。所有资源（图标）嵌入 exe，无需外部文件。

## 📁 项目结构

```
OneBox/
├── src/
│   ├── App.cs              # 入口、单实例、全局异常
│   ├── MainWindow.cs       # 悬浮窗主界面、数据加载与渲染
│   ├── AppResources.cs     # 系统字体 + 嵌入资源加载
│   ├── LauncherBar.cs      # 快捷启动栏（拖拽 / .lnk 解析）
│   ├── WindowScaling.cs    # 分辨率缩放 + 固定位置
│   ├── TrayController.cs   # 系统托盘图标与菜单
│   ├── SettingsDialog.cs   # 统一设置窗口（标签页）
│   ├── AudioDevices.cs     # 音频设备枚举 / 切换 / 热插拔监听
│   ├── VolumeControl.cs    # 音量控制
│   ├── PowerPlanService.cs # 电源计划
│   ├── MemoryCleaner.cs    # 内存清理（NT Native API）
│   ├── TranslateService.cs # 百度翻译 API
│   ├── Dialogs.cs          # 翻译窗口、快捷键捕获、统一窗口样式
│   ├── ClipboardHistory.cs # 剪贴板历史（DPAPI 加密）
│   ├── UpdateChecker.cs    # GitHub Release 自动更新
│   ├── Native.cs           # Win32 P/Invoke
│   ├── Prefs.cs            # 注册表配置存储
│   ├── AdminUtils.cs       # 管理员权限 / 提权
│   ├── Models.cs           # 数据模型
│   ├── build.bat           # 构建脚本
│   ├── app.manifest        # DPI 感知 / UAC
│   └── app.config          # 运行时配置
├── .gitignore
├── LICENSE
└── README.md
```

## ⚙️ 配置与数据

| 内容 | 位置 |
|------|------|
| 应用设置 | 注册表 `HKCU\Software\PowerAudioManager\App` |
| 设备热键 / 隐藏 | 注册表 `HKCU\Software\PowerAudioManager\Devices` |
| 翻译 API Key | 注册表，DPAPI（CurrentUser）加密 |
| 剪贴板历史 | `%LocalAppData%\OneBox\`（DPAPI 加密） |
| 崩溃日志 | `%TEMP%\pam_crash.log` |
| 运行日志 | exe 同目录 `OneBox.log`（截图/清理/热键/音频/电源/更新等） |

## 🔄 自动更新配置（自建分支）

`src\UpdateChecker.cs` 顶部改成你的 GitHub 仓库：

```csharp
public const string Owner = "OneT1er";
public const string Repo = "OneBox";
public static readonly Version CurrentVersion = new Version(1, 1, 0);
```

发新版时在 GitHub 创建 Release，tag 用 `v1.2.0` 格式（程序解析其中的版本号与 `CurrentVersion` 对比）。若 Release 附带 `OneBox.exe` 资产，支持应用内下载并自动替换升级；否则打开 Release 页面手动下载。

## 🛠️ 开发说明

- 用系统自带的 `csc.exe`（C# 5 编译器）构建，**暂不支持 C# 6+ 语法**（无表达式体成员、字符串插值、`?.`、`nameof` 等）。lambda 和 `var` 可用。
- WPF + WinForms 混用：主界面和对话框用 WPF，系统托盘用 WinForms（NotifyIcon）。
- 音频切换通过 MMDevice API + IPolicyConfig COM 接口实现。
- 字体改用系统字体（设置里可选），不再打包字体文件。
- 内存清理使用 NT Native API（`NtSetSystemInformation`），与 memreduct 同源。

## 📝 致谢

- [memreduct](https://github.com/henrypp/memreduct) — 内存清理的 NT API 实现参考
- 百度翻译 API — 翻译服务
- 紫影主题 #8E8CD8

## 📄 许可证

本项目代码采用 [MIT 许可证](LICENSE) 开源。

## 🤝 贡献

欢迎提 Issue 反馈 bug 或建议功能，也欢迎 Pull Request。
