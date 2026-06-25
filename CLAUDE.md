# OneBox

Windows 桌面悬浮工具箱（C# WPF + WinForms，.NET 8），集成电源计划/音频控制/内存清理/翻译/图片翻译/快捷启动/剪贴板历史/前台截图到一个可折叠悬浮窗 + 系统托盘。紫影主题 #8E8CD8，深色圆角卡片，单文件 exe（框架依赖）。仓库 https://github.com/OneT1er/OneBox ，当前版本 **v1.3.1**（beta：v1.3.0-beta2）。

## 构建（重要）

- **.NET 8 + .csproj + NuGet + dotnet CLI**（2026-06 从 .NET Framework 4 + 裸 csc 迁移而来）。项目文件 `src/OneBox.csproj`。
- 开发：`dotnet build src/OneBox.csproj -c Debug` → `src/bin/Debug/net8.0-windows10.0.19041.0/win-x64/OneBox.exe`。
- 发布（框架依赖单文件）：`dotnet publish src/OneBox.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true` → `publish/OneBox.exe`。
- `dotnet` 不在默认 PATH，bash 调用前 `export PATH="/c/Program Files/dotnet:$PATH"`。.NET 8 SDK 8.0.422 装在 `C:\Program Files\dotnet`。
- **现代 C#**（LangVersion=latest）：内插字符串、out var、表达式体等都可用。
- **NuGet**：`Vortice.DXGI`（HDR 检测）、`System.Text.Encoding.CodePages`（GBK 编码，电源计划/升级脚本必需）。新增 `.cs` 自动参与编译（SDK 风格）。
- **调试路径陷阱**：csproj 设了 `RuntimeIdentifier=win-x64`，debug 输出在 `bin/Debug/<tfm>/win-x64/`，不是 `bin/Debug/<tfm>/`。跑错路径会跑旧二进制。
- exe 被占用（OneBox 在跑，**提权实例杀不掉**需用户手动关）会 MSB3027 锁定错误。
- 资源用 `<EmbeddedResource>`，逻辑名 `PowerAudioManager.<file>`，`GetManifestResourceStream("PowerAudioManager."+file)` 加载。
- `Assembly.Location` 在单文件发布返回空，全用 `Environment.ProcessPath`。

## git 与发布

- 用户 OneT1er，本仓库 local git：`user.name=OneT1er` / `user.email=liuxy1122@outlook.com`。本仓库已关代理直连 GitHub（全局 7897 代理常没开）。
- 发版：升 `src/UpdateChecker.cs` 的 `CurrentVersion` + 同步 README → 更新 `CHANGELOG.md` → `dotnet publish` → `git commit` + `git tag vX.Y.Z` + push main + push tag。
- **无 gh CLI**：取 token `printf "protocol=https\nhost=github.com\n\n" | git credential fill | grep password`（返回 `gho_...`），用 **`py`**（Python 3.13；`python3`/`python` 是 Store stub 不能用）调 GitHub API 创建 release + 上传 OneBox.exe 资产。UpdateChecker 从最新 release 的 exe 资产做应用内升级。预发布版本不进 `releases/latest`，不影响稳定用户。

## 工作偏好

- **直接改代码，不写 spec/计划文档**（用户多次明确「直接开始」）。多方案决策用 AskUserQuestion 一次问清。
- 编译后**冒烟测试**：后台启动 exe，sleep 4-6 秒，`tasklist | grep onebox` 确认存活再 taskkill。真实功能验证靠用户实机跑。
- 改完通常 commit + push；发版要 build + tag + release + 上传 exe。
- 出 bug 用户常贴 `OneBox.log`（exe 同目录）内容定位。

## 架构要点

- 模块拆分（v1.2.0）：MainWindow + AppResources(字体/资源/共享深色样式) + LauncherBar + WindowScaling + TrayController + SettingsDialog + ScreenshotService/ScreenshotToast/ScreenshotGallery + Dialogs。配置走注册表 `HKCU\Software\PowerAudioManager\App`（AppPrefs）。
- **DPI**：`<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`（csproj 配，.NET 8 会从 manifest 剥离 DPI 设置）。ApplyScaling 除以实时 DPI 避免叠加过大。曾误降级系统级导致切 4K 模糊，已修复。
- **固定位置（无条件）**：拖到哪固定到哪，切分辨率不动（存绝对 Left/Top），仅完全离开屏幕才夹回。与"锁定位置"（LockPosition 禁拖）是两个独立开关。
- **自动折叠**：默认开，离开延时折叠/悬停展开；手动折叠后保持折叠不自动展开（`_collapsedManually`）。折叠时标题栏改全圆角。
- **截图**：CopyFromScreen 截前台窗口客户区。高级 HDR 截图（默认关，`Screenshot.GameBarEnabled`）：Vortice.DXGI 检测 HDR 显示器，HDR/全屏游戏回退 Game Bar（可配置快捷键绕开"游戏吞 Win 键"，可配置 Game Bar 读取位置）。jxr 保留。按应用 exe 名建子目录。Toast 右下角弹窗(WS_EX_NOACTIVATE 不抢焦点)。
- **图片翻译**：`Screenshot.ImageTranslateHotkey` 触发 `RegionCaptureService`（全屏透明遮罩拖框，AllowsTransparency 必须开否则截全黑）→ `ImageTranslateService`（百度图片翻译 paste=1，复用文本翻译 AppId/Key Bearer 鉴权）→ `ImageTranslateWindow`（贴合图 + 复制译文）。
- **热键 ID**：TRANSLATE=0xBFFF(固定Ctrl+Shift+T)、SCREENSHOT=0xBFFE、CLIPBOARD=0xBFD0、IMAGE_TRANSLATE=0xBFD1、设备 BASE=0xB000、测试 0xBE00。设置时 TestHotkey 试注册检测占用。
- **启动**（v1.3.0）：PerformanceCounter 后台预热（`WarmupCounters`，.NET 8 首次创建 ~5s）；LoadData 延迟用 `System.Threading.Timer`（ApplicationIdle 在 .NET 8 冷启动被推迟 ~6s）；`Encoding.RegisterProvider(CodePagesEncodingProvider)` 在 Main 最前（GBK 936，电源计划/升级脚本必需）。

## 已知坑（详见 .claude/memory/onebox-*.md）

- **.NET 8 迁移回归**（详见 onebox-migration-gotchas）：GBK 编码 936 抛异常（RegisterProvider 修复）；PerformanceCounter 首次创建 ~5s（后台预热）；ApplicationIdle 冷启动推迟 ~6s（改 threading timer）；csproj 设 RID 后 debug 输出在 win-x64 子目录（跑错路径跑旧二进制）；手写 DXGI vtable 索引错 → AccessViolation 闪退（改 Vortice 投影，绝不手写 COM vtable）。
- **游戏前台吞 Win 键**：注入的 Win 键被系统/Game Mode 吞（GetAsyncKeyState 读 0x0000），Game Bar Win+Alt+PrtScn 触发不了。解决：Game Bar 快捷键改不含 Win 的组合（Alt+F12），OneBox 注入同款。被 Game Bar 注册的 Alt+ 组合在 OneBox UI 捕获不到，用 Ctrl+ 组合。
- **框选截图全黑**：遮罩窗口必须 `AllowsTransparency=true`，否则半透明背景渲染成纯黑，CopyFromScreen 截到遮罩。
- **XamlReader.Parse 不认 `x:Name`** → 抛 XamlParseException 被吞，设置窗口不弹。**用 `Name`**。
- **中文路径 `BitmapImage.UriSource` 失败** → 图库空白/Toast 无缩略图。**用 `StreamSource + FileStream`**。截图根目录含中文"图片"。
- **控件未加父容器** → 孤儿不显示。组装 UI 确认都 Add 了。
- **`Process.MainModule` 对提权/UWP 进程访问拒绝** → 截图存 Unknown 文件夹。**用 `QueryFullProcessImageName(PROCESS_QUERY_LIMITED_INFORMATION)`**。
- **powercfg ReadToEnd 死锁** → 用 `BeginOutputReadLine`；编码用系统 OEM(`GetOEMCP`)，.NET 8 需 RegisterProvider。
- **COM 对象**（WScript.Shell）必须 finally `Marshal.ReleaseComObject`。

## 详细记忆

更完整的分条记忆在 `.claude/memory/`（每文件一事实，含 frontmatter）：overview / build-constraints / user-workflow / architecture / pitfalls / hdr-screenshot / migration-gotchas。
