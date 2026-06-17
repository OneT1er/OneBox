# OneBox

一个 Windows 桌面悬浮工具箱：电源计划切换、音频设备切换、内存清理、翻译、快捷启动栏、剪贴板历史，集成在同一个可拖拽的悬浮窗 + 系统托盘里。

## 特性

- **悬浮窗**：紫影主题，圆角卡片，可拖拽、可置顶、可锁定位置、可折叠
- **电源计划**：一键切换 Windows 电源方案
- **音频控制**：切换默认输出设备、调节音量、静音，支持设备热插拔
- **内存清理**：空工作集 / 系统文件缓存 / standby list 等（部分需管理员）
- **翻译**：百度大模型翻译 API，独立窗口，全局快捷键 Ctrl+Shift+T 翻译剪贴板
- **快捷启动栏**：4 格，点击添加程序，自动提取图标
- **剪贴板历史**：记录最近 20 条，持久化，点击即复制
- **模块化开关**：在"板块设置"里隐藏不需要的板块
- **动态托盘图标**：闪电 Logo 按内存负载变色（绿/黄/红）
- **自动更新**：启动时后台检查 GitHub Release，托盘菜单可手动检查
- **HarmonyOS Sans SC 字体**：随程序分发，无需系统安装

## 构建

需要 Windows + .NET Framework 4.x（系统自带 `csc.exe`）。

1. 双击 `src\build.bat`，或在 cmd 里运行：
   ```
   cd src
   build.bat
   ```
2. 输出在 `src\output\OneBox.exe`

HarmonyOS Sans SC 字体已随仓库分发（`src\HarmonyOS_Sans_SC_Regular.ttf`），构建时自动复制到输出目录，开箱即用。

## 自动更新配置

`src\UpdateChecker.cs` 顶部有两个常量需要改成你自己的 GitHub 仓库：

```csharp
public const string Owner = "YOUR_GITHUB_USERNAME";
public const string Repo = "OneBox";
```

发新版时：在 GitHub 上创建一个 Release，tag 用 `v1.2.0` 这种格式（`UpdateChecker` 会解析其中的版本号与 `CurrentVersion` 对比）。程序启动会静默检查；有新版弹窗提示，点"前往下载"打开 Release 页面。

## 配置存储

设置保存在注册表 `HKCU\Software\PowerAudioManager\`。翻译 API Key 用 DPAPI（CurrentUser 范围）加密。剪贴板历史存 `%LocalAppData%\OneBox\clipboard.txt`。

## 开发约束

本项目用系统自带的旧版 `csc.exe`（C# 5 编译器）构建，**不能用 C# 6+ 语法**（无表达式体成员、字符串插值、`?.`、`nameof` 等）。lambda 和 `var` 可用。
