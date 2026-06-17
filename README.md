# ⚡ OneBox

> 一个 Windows 桌面悬浮工具箱，把电源管理、音频控制、内存清理、翻译、快捷启动、剪贴板历史集成进一个可拖拽的悬浮窗 + 系统托盘。

紫影主题、圆角卡片、深色 UI，常驻任务栏，鼠标就近操作。开箱即用，无需安装运行时。

<!-- 截图占位：把悬浮窗截图放到 docs/ 下并取消注释
![OneBox 悬浮窗](docs/screenshot.png)
-->

## ✨ 功能

### 悬浮窗
- **紫影主题**：圆角卡片，深色界面，HarmonyOS Sans SC 字体（随程序分发）
- **可拖拽**：按住标题栏拖到任意位置，位置自动记忆
- **可置顶**：托盘菜单切换窗口置顶
- **可锁定**：锁定位置防止误拖
- **可折叠**：点击折叠按钮向上收起，只留标题栏
- **鼠标滚轮**：在悬浮窗上滚动直接调音量
- **悬浮提示**：折叠状态下悬停标题栏显示电源/音频/内存概览

### 电源计划
一键切换 Windows 电源方案（平衡 / 高性能 / 节能 / 自定义），双击按钮打开电源设置。

### 音频控制
- 切换默认输出设备（音箱 / 耳机 / 蓝牙等）
- 音量滑块、静音、实时音量显示
- 支持设备热插拔，插拔后自动刷新
- 每个设备可绑定全局快捷键
- 隐藏不常用的设备

### 内存清理
- 可选清理项：Working set、System file cache、Standby list、Modified page list、Registry cache、Combine memory lists
- 按时间周期 / 按内存占用率自动清理
- 非管理员也能清理部分项目；管理员可启用全部
- 动态托盘图标按内存负载变色（绿 < 60% / 黄 60–80% / 红 > 80%）

### 翻译
- 百度大模型翻译 API，独立窗口
- 语言自动检测，支持中 / 英 / 日 / 韩 / 法 / 德 / 俄 / 西 / 阿
- 长文本自动分块，避免 API 长度限制
- 全局快捷键 `Ctrl+Shift+T` 一键翻译剪贴板内容
- 翻译指令可自定义（意译 / 商务语气 / 保留术语等）
- API Key 用 DPAPI 加密存储

### 快捷启动栏
4 格启动栏，点击空格选择程序（.exe / .lnk），自动提取图标；点击图标启动；右键清空。

### 剪贴板历史
- 记录最近 20 条复制内容，去重
- 持久化到磁盘，重启后保留
- 点击悬浮窗按钮弹出历史列表，点击即复制回剪贴板

### 模块化开关
在"板块设置"里自由隐藏不需要的板块（电源 / 音频 / 内存 / 翻译 / 启动栏 / 剪贴板），悬浮窗即时刷新。

### 自动更新
- 启动时后台静默检查 GitHub Release
- 托盘菜单"检查更新"手动触发
- 发现新版本弹窗显示更新内容，一键打开下载页

## ⌨️ 快捷键

| 快捷键 | 功能 |
|--------|------|
| `Ctrl+Shift+T` | 翻译剪贴板内容 |
| `Ctrl+Shift+数字`（可自定义） | 切换到指定音频设备 |
| 鼠标滚轮（悬浮窗上） | 调节音量 |

热键可在音频设备项上点击设置，支持冲突检测与覆盖。

## 🖱️ 托盘操作

| 操作 | 功能 |
|------|------|
| 左键单击托盘图标 | 显示 / 隐藏悬浮窗 |
| 右键单击托盘图标 | 打开菜单 |
| 中键单击托盘图标 | 立即清理内存 |

## 🚀 下载使用

1. 前往 [Releases](../../releases) 下载最新的 `OneBox.exe`
2. 双击运行，无需安装
3. 开机自启可在托盘菜单"开机自启"里开启

> 需要完整内存清理功能（Standby list 等）时，用托盘菜单"以管理员身份重启"。

## 🔧 构建

需要 Windows + .NET Framework 4.x（系统自带 `csc.exe`，无需额外安装）。

```
cd src
build.bat
```

输出在 `src\output\OneBox.exe`。

HarmonyOS Sans SC 字体已随仓库分发（`src\HarmonyOS_Sans_SC_Regular.ttf`），构建时自动复制到输出目录。

## 📁 项目结构

```
OneBox/
├── src/
│   ├── App.cs              # 入口、单实例、全局异常
│   ├── MainWindow.cs       # 悬浮窗主界面、托盘、动态图标、启动栏
│   ├── AudioDevices.cs     # 音频设备枚举 / 切换 / 热插拔监听
│   ├── VolumeControl.cs    # 音量控制
│   ├── PowerPlanService.cs # 电源计划
│   ├── MemoryCleaner.cs    # 内存清理
│   ├── TranslateService.cs # 百度翻译 API
│   ├── Dialogs.cs          # 翻译窗口、设置对话框、统一窗口样式
│   ├── ClipboardHistory.cs # 剪贴板历史
│   ├── UpdateChecker.cs    # GitHub Release 自动更新
│   ├── Native.cs           # Win32 P/Invoke
│   ├── Prefs.cs            # 注册表配置存储
│   ├── AdminUtils.cs       # 管理员权限 / 提权
│   ├── Models.cs           # 数据模型
│   ├── build.bat           # 构建脚本
│   ├── app.manifest        # DPI 感知 / UAC
│   └── app.config          # 运行时配置
├── .gitignore
└── README.md
```

## ⚙️ 配置与数据

| 内容 | 位置 |
|------|------|
| 应用设置 | 注册表 `HKCU\Software\PowerAudioManager\App` |
| 设备热键 / 隐藏 | 注册表 `HKCU\Software\PowerAudioManager\Devices` |
| 翻译 API Key | 注册表，DPAPI（CurrentUser）加密 |
| 剪贴板历史 | `%LocalAppData%\OneBox\clipboard.txt` |
| 崩溃日志 | `%TEMP%\pam_crash.log` |
| 调试日志 | `%TEMP%\pam_debug.log` |

## 🔄 自动更新配置（自建分支）

`src\UpdateChecker.cs` 顶部改成你的 GitHub 仓库：

```csharp
public const string Owner = "YOUR_GITHUB_USERNAME";
public const string Repo = "OneBox";
public static readonly Version CurrentVersion = new Version(1, 0, 0);
```

发新版时在 GitHub 创建 Release，tag 用 `v1.2.0` 格式（程序解析其中的版本号与 `CurrentVersion` 对比）。有新版会弹窗提示，点"前往下载"打开 Release 页面。

## 🛠️ 开发说明

- 用系统自带的 `csc.exe`（C# 5 编译器）构建，**暂不支持 C# 6+ 语法**（无表达式体成员、字符串插值、`?.`、`nameof` 等）。lambda 和 `var` 可用。
- WPF + WinForms 混用：主界面和对话框用 WPF，系统托盘用 WinForms（NotifyIcon）。
- 音频切换通过 MMDevice API + IPolicyConfig COM 接口实现。
- 字体通过 `PrivateFontCollection` 私有加载，不污染系统字体目录。

## 📝 致谢

- [HarmonyOS Sans SC](https://developer.harmonyos.com/cn/design/resource/) — 华为鸿蒙字体
- 百度翻译 API — 翻译服务
- 紫影主题 #8E8CD8

## 📄 许可证

本项目代码仅供学习交流使用。HarmonyOS Sans SC 字体版权归华为所有，按其原始许可使用。
