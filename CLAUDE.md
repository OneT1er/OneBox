# OneBox

Windows 桌面悬浮工具箱（C# WPF + WinForms），集成电源计划/音频控制/内存清理/翻译/快捷启动/剪贴板历史/前台截图到一个可折叠悬浮窗 + 系统托盘。紫影主题 #8E8CD8，深色圆角卡片，单文件 exe。仓库 https://github.com/OneT1er/OneBox ，当前版本 **v1.2.1**。

## 构建（重要）

- 用 `src/build.bat` 直接调 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`，**无 .csproj / 无 MSBuild / 无 NuGet**。
- **C# 5 语法**：不能用表达式体成员、字符串插值 `$""`、`?.`、`nameof` 等 C#6+。lambda、`var` 可用。**不能用 `unsafe`**（无 `/unsafe`）。
- **新增 `.cs` 必须手动加到 build.bat 编译列表**，否则不参与编译。
- 单文件 exe：图标 `/resource` 嵌入；不打包字体（改系统字体）；不引外部 dll。
- 编译命令（bash 里必须绕过 MSYS 路径转换）：
  ```
  taskkill //F //IM OneBox.exe 2>/dev/null
  MSYS_NO_PATHCONV=1 cmd.exe /c "C:\Users\LIUxy\OneDrive\Documents\OneBox\src\build.bat"
  ```
  输出 `src/output/OneBox.exe`。
- exe 被占用（OneBox 在跑，**提权实例杀不掉**需用户手动关）会 CS0016。

## git 与发布

- 用户 OneT1er，本仓库 local git：`user.name=OneT1er` / `user.email=liuxy1122@outlook.com`。本仓库已关代理直连 GitHub（全局 7897 代理常没开）。
- 发版：升 `src/UpdateChecker.cs` 的 `CurrentVersion` + 同步 README → 更新 `CHANGELOG.md` → build.bat 编译 → `git commit` + `git tag vX.Y.Z` + push main + push tag。
- **无 gh CLI**：取 token `printf "protocol=https\nhost=github.com\n\n" | git credential fill | grep password`（返回 `gho_...`），用 **`py`**（Python 3.13；`python3`/`python` 是 Store stub 不能用）调 GitHub API 创建 release + 上传 OneBox.exe 资产。UpdateChecker 从最新 release 的 exe 资产做应用内升级。

## 工作偏好

- **直接改代码，不写 spec/计划文档**（用户多次明确「直接开始」）。多方案决策用 AskUserQuestion 一次问清。
- 编译后**冒烟测试**：后台启动 exe，sleep 4-6 秒，`tasklist | grep onebox` 确认存活再 taskkill。真实功能验证靠用户实机跑。
- 改完通常 commit + push；发版要 build + tag + release + 上传 exe。
- 出 bug 用户常贴 `OneBox.log`（exe 同目录）内容定位。

## 架构要点

- 模块拆分（v1.2.0）：MainWindow + AppResources(字体/资源/共享深色样式) + LauncherBar + WindowScaling + TrayController + SettingsDialog + ScreenshotService/ScreenshotToast/ScreenshotGallery + Dialogs。配置走注册表 `HKCU\Software\PowerAudioManager\App`（AppPrefs）。
- **DPI**：manifest `PerMonitorV2,PerMonitor`。ApplyScaling 除以实时 DPI 避免叠加过大。曾误降级系统级导致切 4K 模糊，已修复。
- **固定位置（无条件）**：拖到哪固定到哪，切分辨率不动（存绝对 Left/Top），仅完全离开屏幕才夹回。与"锁定位置"（LockPosition 禁拖）是两个独立开关。
- **自动折叠**：默认开，离开延时折叠/悬停展开；手动折叠后保持折叠不自动展开（`_collapsedManually`）。折叠时标题栏改全圆角。
- **截图**：CopyFromScreen 截前台窗口客户区，全黑(≥99%近黑)回退 Game Bar(Win+Alt+PrtScn，监视 Captures 取最新 png 移动)。按应用 exe 名建子目录。Toast 右下角弹窗(WS_EX_NOACTIVATE 不抢焦点)。
- **热键 ID**：TRANSLATE=0xBFFF(固定Ctrl+Shift+T)、SCREENSHOT=0xBFFE、CLIPBOARD=0xBFD0、设备 BASE=0xB000、测试 0xBE00。设置时 TestHotkey 试注册检测占用。
- **启动优化**（v1.2.1）：LoadData/UpdateIcon 延迟到 OnLoaded 后 ApplicationIdle 异步（首次 PerformanceCounter init ~300ms）。窗口可见 ~420ms。

## 已知坑（详见 .claude/memory/onebox-pitfall-*.md）

- **XamlReader.Parse 不认 `x:Name`** → 抛 XamlParseException 被吞，设置窗口不弹。**用 `Name`**。
- **中文路径 `BitmapImage.UriSource` 失败** → 图库空白/Toast 无缩略图。**用 `StreamSource + FileStream`**。截图根目录含中文"图片"。
- **控件未加父容器** → 孤儿不显示（图库空白真因之一）。组装 UI 确认都 Add 了。
- **PerformanceCounter 首次 ~300ms** → 曾阻塞启动。已延迟异步。
- **`Process.MainModule` 对提权/UWP 进程访问拒绝** → 截图存 Unknown 文件夹。**用 `QueryFullProcessImageName(PROCESS_QUERY_LIMITED_INFORMATION)`**。
- **powercfg ReadToEnd 死锁** → 用 `BeginOutputReadLine`；编码用系统 OEM(`GetOEMCP`)。
- **COM 对象**（WScript.Shell）必须 finally `Marshal.ReleaseComObject`。
- bash 里 cmd.exe 调用用 `MSYS_NO_PATHCONV=1`；`//nologo` 会被吃，用 `/nologo`。

## 详细记忆

更完整的分条记忆在 `.claude/memory/`（每文件一事实，含 frontmatter），含模块职责、热键编码、各坑的完整 P/Invoke 等。
