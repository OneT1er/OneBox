<p align="center">
  <img src="assets/icons/app-preview.png" width="112" alt="OneBox 图标">
</p>

<h1 align="center">OneBox</h1>

<p align="center">
  <strong>把常用的 Windows 工具，收进一个随手可用的悬浮窗。</strong>
  <br>
  电源、音频、内存、性能、翻译、截图、剪贴板与快捷启动，一处完成。
</p>

<p align="center">
  <a href="https://github.com/OneT1er/OneBox/releases/latest"><img src="https://img.shields.io/github/v/release/OneT1er/OneBox?color=8E8CD8&label=release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Windows-10%20%7C%2011-258FFA?logo=windows11&logoColor=white" alt="Windows 10 / 11">
  <img src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 10">
  <a href="LICENSE"><img src="https://img.shields.io/github/license/OneT1er/OneBox?color=8E8CD8" alt="MIT License"></a>
</p>

<p align="center">
  <a href="https://github.com/OneT1er/OneBox/releases/latest"><strong>下载最新版</strong></a>
  ·
  <a href="#功能概览">功能概览</a>
  ·
  <a href="#安装与使用">安装说明</a>
  ·
  <a href="#开发与构建">参与开发</a>
</p>

---

## 为什么是 OneBox

切电源方案、换耳机、清内存、看温度、翻译一段文字——这些操作本来散落在系统设置和不同软件里。OneBox 将它们集中到桌面边缘的可折叠悬浮窗中：不抢任务栏、不打断当前工作，需要时靠近即可。

| | |
|---|---|
| 🪟 **轻巧悬浮**<br>自动折叠、窗口置顶、位置锁定、多显示器与 DPI 适配 | 🔊 **音频与电源**<br>切换默认输出设备、调音量、静音、一键切换电源计划 |
| 🧹 **内存管理**<br>实时占用、系统缓存、手动清理、定时与阈值自动清理 | 📈 **性能监控**<br>温度、风扇、趋势曲线、历史记录与前台应用时间段 |
| 🌐 **翻译与截图**<br>文本翻译、框选图片翻译、前台窗口截图、HDR 游戏回退与安全外部截图接管 | 📋 **效率工具**<br>加密剪贴板历史、8 格快捷启动栏、全局快捷键 |

## 功能概览

### 悬浮窗体验

- 深色圆角卡片与统一紫影主题，可选择系统字体。
- 鼠标离开后自动折叠，悬停恢复；也可手动保持折叠。
- 拖到哪里就固定在哪里，切换分辨率或缩放后不会随意漂移。
- 支持窗口置顶、锁定位置、模块显隐以及悬浮窗滚轮调音量。
- 常驻系统托盘：左键显示、右键打开菜单、中键立即清理内存。

<details>
<summary><strong>音频、电源与内存</strong></summary>

### 音频控制

- 枚举并切换音箱、耳机、蓝牙等默认输出设备。
- 音量滑块、静音、实时音量显示和设备热插拔刷新。
- 每个设备均可绑定全局快捷键，也可隐藏不常用设备。

### 电源计划

- 一键切换平衡、高性能、节能或自定义 Windows 电源方案。
- 双击入口可直接打开系统电源设置。
- 按系统 OEM 编码解析 `powercfg`，兼容不同语言的 Windows。

### 内存清理

- 支持 Working Set、System File Cache、Standby List、Modified Page List、Registry Cache 等清理项。
- 支持按时间周期或内存占用率自动清理。
- 危险清理项默认跳过并提供额外确认，避免误操作引起短暂卡顿。
- 普通操作无需管理员权限；特权清理由独立服务处理。

</details>

<details>
<summary><strong>性能趋势与硬件数据</strong></summary>

- 显示 CPU、GPU、主板、内存、硬盘温度以及风扇转速。
- 趋势图支持 5 分钟至全天多个时间范围、双 Y 轴、十字线与数值提示。
- 图表可标记各时间段的前台应用，方便对照负载变化。
- 只记录真实传感器读数；停采、重启或传感器失配会显示断口，不用旧值填充。
- 后台持续采集并每 60 秒落盘，打开图表即可查看此前记录。
- 硬件采集由隔离进程完成，通过 SID 隔离的命名管道传输。

</details>

<details>
<summary><strong>翻译、截图与效率工具</strong></summary>

### 翻译

- 百度大模型文本翻译，支持中、英、日、韩、法、德、俄、西、阿等语言。
- 自动检测源语言，长文本自动分块，可自定义翻译指令。
- `Ctrl+Shift+T` 直接翻译剪贴板文本。
- 自定义快捷键框选屏幕区域，返回擦除原文并贴合译文的翻译图片。
- API 凭据使用 Windows DPAPI 加密保存。

### 截图

- 截取前台窗口客户区，并按应用名称自动分类。
- 截图完成后显示不抢焦点的右下角提示，并提供图库查看。
- 可选 HDR / 全屏游戏 Game Bar 回退，快捷键与读取目录均可配置。
- 对有反作弊保护的游戏，可启用“外部截图接管”：由 Game Bar、Steam、显卡工具或游戏自身完成截图，OneBox 仅监听新文件并复制归档，不模拟按键或注入游戏进程。
- 外部接管支持自定义目录与子目录、常见图片格式及 HDR PNG/JXR 配对，并保留官方截图原文件。

### 快捷启动与剪贴板

- 8 格启动栏支持程序、快捷方式、文件夹、URL 与拖放添加。
- 记录文本和图片剪贴板历史，按内容去重，点击即可再次复制。
- 剪贴板历史使用 DPAPI 加密持久化，重启后仍可恢复。

</details>

## 快捷操作

| 操作 | 默认行为 |
|---|---|
| `Ctrl+Shift+T` | 翻译剪贴板文本 |
| 自定义截图快捷键 | 截取前台窗口客户区 |
| 自定义图片翻译快捷键 | 框选区域并翻译 |
| 自定义音频设备快捷键 | 切换到指定输出设备 |
| 悬浮窗内滚轮 | 调节系统音量 |
| 托盘图标左键 | 显示悬浮窗 |
| 托盘图标中键 | 立即清理内存 |
| 托盘图标右键 | 打开快捷菜单 |

设备热键支持占用检测与覆盖确认，可在设置中随时修改。

## 安装与使用

### 推荐：安装版

1. 打开 [Latest Release](https://github.com/OneT1er/OneBox/releases/latest)。
2. 下载并运行 `OneBox-win-Setup.exe`。
3. 首次启动后，在“设置”中选择需要显示的模块、热键与开机自启。

安装版支持 Velopack 目录级自动更新。安装器会声明 .NET 10 x64 Desktop Runtime 要求；系统缺少运行时时请按提示安装。

### 便携版

Release 同时提供 `OneBox-win-Portable.zip`，解压即可运行，适合临时体验。便携环境不会执行应用内自动更新，升级时请重新下载完整压缩包。

> 完整的 Standby List 等特权内存清理能力依赖 OneBox 服务。若服务尚未安装，可从托盘菜单选择“以管理员身份重启”。

## 设置与本地数据

| 内容 | 保存位置 |
|---|---|
| 应用设置 | `HKCU\Software\PowerAudioManager\App` |
| 音频设备热键 / 隐藏状态 | `HKCU\Software\PowerAudioManager\Devices` |
| 翻译凭据 | 当前用户注册表，DPAPI 加密 |
| 剪贴板历史 | `%LocalAppData%\OneBox\`，DPAPI 加密 |
| 截图与图库 | 用户在设置中选择的目录 |
| 运行日志 | 应用目录下的 `OneBox.log` |
| 服务 / 硬件日志 | `OneBox.Service.log` / `OneBox.Hardware.log` |

OneBox 不提供云端账户。只有在主动使用百度翻译功能时，待翻译内容才会按照百度 API 请求发送。

## 运行架构

```text
                         SID 隔离命名管道
┌────────────────┐      ┌────────────────────┐
│   OneBox.exe   │ ◄──► │ OneBox.Service.exe │
│ WPF 悬浮窗 / 托盘 │      │ 会话启动 / 特权清理  │
└───────┬────────┘      └─────────┬──────────┘
        │                           │ 守护
        └──────────────────────────►│
                          ┌─────────▼──────────┐
                          │OneBox.Hardware.exe │
                          │   硬件传感器采集     │
                          └────────────────────┘
```

界面、特权操作与硬件采集分进程运行。这样可以让日常 GUI 保持普通权限，同时隔离高权限操作和第三方硬件库。

## 开发与构建

### 环境

- Windows 10 / 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- PowerShell

仓库通过 `global.json` 固定 SDK，公共版本号位于 `Directory.Build.props`。

```powershell
dotnet restore OneBox.sln
dotnet build OneBox.sln -c Debug
dotnet test OneBox.sln -c Release
```

普通开发构建会把 GUI、Service 与 Hardware 三个进程组合到 GUI 输出目录。完整打包使用：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/package.ps1
```

Velopack 产物输出到 `artifacts/packages/win-x64`。清理所有编译、测试、发布与打包产物：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/clean.ps1
```

### 目录结构

```text
OneBox/
├── src/
│   ├── OneBox.csproj          # WPF GUI
│   ├── OneBox.Contracts/      # IPC DTO、帧协议与安全约束
│   ├── OneBox.Service/        # Windows Service
│   ├── OneBox.Hardware/       # 硬件采集进程
│   ├── Commands/              # 统一命令入口与调度
│   └── Shared/                # 共享的 Windows / IPC 基础设施
├── tests/OneBox.Tests/        # xUnit 测试
├── assets/icons/              # 原创图标与设计资源
├── scripts/                   # 打包与清理脚本
├── Directory.Build.props      # 公共构建属性与版本
├── Directory.Packages.props   # NuGet 版本集中管理
└── OneBox.sln
```

更多发布变化见 [CHANGELOG.md](CHANGELOG.md)。

## 贡献

Bug、兼容性问题和功能建议都欢迎提交 [Issue](https://github.com/OneT1er/OneBox/issues)。准备提交代码时，请先运行 Debug 构建与 Release 测试。

## 致谢

- [memreduct](https://github.com/henrypp/memreduct) — NT Native 内存清理实现参考
- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) — 硬件传感器数据
- [NAudio](https://github.com/naudio/NAudio) — Windows 音频设备与音量控制
- [Velopack](https://github.com/velopack/velopack) — 安装与自动更新
- 百度翻译 API — 文本与图片翻译

## 许可证

OneBox 使用 [MIT License](LICENSE) 开源。
