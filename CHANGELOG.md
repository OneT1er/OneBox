# 更新日志

## v1.3.1 (2026-06-25)

> 首个 .NET 8 正式版。集合两个 beta 的所有新特性，并全面重构 UI 为 Material Design 3（紫影深色主题）。

### 新功能
- **.NET 8 迁移**：框架依赖单文件发布，现代 C#，NuGet 包管理（Vortice.DXGI / CodePages）
- **HDR 截图（高级）**：Vortice.DXGI 检测 + Game Bar 回退，jxr 保留，默认关闭
- **图片翻译**：自定义热键 → 拖框选区 → 百度图片翻译 API → 擦除原文贴合译文整图，可复制译文
- **剪贴板历史**：全局热键弹出，左键复制 / 右键删除单条
- **UI 重构**：全面接入 MaterialDesignInXAML（MD3，紫影 #8E8CD8 深色主题）；悬浮窗按钮改 PackIcon 矢量图标；对话框控件 MD3 化；手写 ControlTemplate 清理净减 ~300 行

### 修复
- .NET 8 GBK 编码 936 抛异常（RegisterProvider 修复）
- 框选截图全屏黑色（遮罩 `AllowsTransparency=true`）
- Game Bar 截图快捷键配置顺序更正
- PerformanceCounter 冷启动 ~5s 冻结（后台预热）
- ApplicationIdle 冷启动推迟 ~6s（改 threading timer）

## v1.3.0-beta2 (2026-06-25) [预发布]

### 新功能：图片翻译
- 框选截图翻译：自定义热键 → 屏幕拖框选区 → 调用百度图片翻译 API → 显示擦除原文、贴合译文的整图
- 百度图片翻译 API（`picture/translate`，paste=1 整图贴合），复用文本翻译的 AppId/Key（Bearer 鉴权）
- 结果窗口：贴合图 + 复制译文 / 选择复制 / Ctrl+滚轮缩放
- 新增模块：`ImageTranslateService`（API）、`RegionCaptureService`（全屏框选遮罩）、`ImageTranslateWindow`（结果 UI）

### 修复
- Game Bar 截图快捷键说明：配置顺序更正为「先在 OneBox 设快捷键，再去 Game Bar 设同款」
- 框选截图全屏黑色：遮罩窗口补 `AllowsTransparency=true`（否则半透明背景被当纯黑渲染，CopyFromScreen 截到遮罩），截图前隐藏遮罩

## v1.3.0-beta1 (2026-06-25) [预发布]

> ⚠️ 预发布版本。重大重构：从 .NET Framework 4 + 裸 csc.exe 迁移到 .NET 8 + .csproj + NuGet。
> 运行需安装 [.NET 8 桌面运行时](https://dotnet.microsoft.com/download/dotnet/8.0)。
> 旧的"单文件 exe / 无运行时依赖"形态变更为"框架依赖单文件 exe"。

### 重大变更：迁移到 .NET 8
- 构建从 `build.bat` + 裸 csc.exe 迁移到 `OneBox.csproj` + `dotnet build`/`dotnet publish`
- C#5 → 现代 C#（内插字符串、out var、表达式体等）
- JSON：`JavaScriptSerializer` → `System.Text.Json`（UpdateChecker / TranslateService）
- `Assembly.Location` → `Environment.ProcessPath`（单文件发布必需，6 处）
- 单文件框架依赖发布（`PublishSingleFile` + ReadyToRun 预编译）
- DPI 感知从 manifest 迁到 `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`
- 引入 NuGet：`Vortice.DXGI`（HDR 检测）、`System.Text.Encoding.CodePages`

### 新功能：HDR 截图（高级，默认关闭）
- Vortice.DXGI 检测前台窗口所在显示器是否 HDR（HDR10，ColorSpace==RGB_FULL_G2084_NONE_P2020）
- 设置加"高级：Game Bar 截图"开关，默认关闭仅普通截图；开启后 HDR/全屏游戏回退 Game Bar
- Game Bar 截图读取位置可配置（解决图库位置被改导致找不到文件）
- Game Bar 截图快捷键可配置（绕开"游戏前台吞 Win 键"——改用不含 Win 的组合如 Alt+F12）
- Game Bar 回退保留 HDR `.jxr` 文件（`WaitForFileReady` 防止复制半写文件）

### 修复
- 电源计划识别不到：.NET 8 默认不支持 GBK(936) 编码，`Encoding.RegisterProvider(CodePagesEncodingProvider)` 修复（同时修复应用内升级 .bat 写入）
- 启动卡顿：PerformanceCounter 首次创建 ~5s 阻塞 UI 线程，改后台预热（`WarmupCounters`）
- LoadData 启动延迟：`DispatcherPriority.ApplicationIdle` 在 .NET 8 冷启动被推迟 ~6s，改 `System.Threading.Timer`
- 截图闪退：手写 DXGI vtable 索引错导致 AccessViolation，改用 Vortice 投影

### 已知限制
- 框架依赖发布：用户机器需装 .NET 8 桌面运行时，否则启动失败
- Game Bar 截图快捷键：被 Game Bar 全局注册的 Alt+ 组合在 OneBox 设置里可能捕获不到，可用 Ctrl+ 组合
- 截图 `.jxr` 缩略图：WPF 不自动色调映射，Toast/图库显示的是 SDR `.png` 预览

## v1.2.1 (2026-06-23)

### 优化
- 启动速度：`LoadData` 与 `_tray.UpdateIcon()` 从构造函数延迟到 `OnLoaded` 后异步执行，避免首次创建 PerformanceCounter（~300ms）阻塞窗口显示
- 进程到窗口可见：约 800ms → 约 420ms，窗口几乎立刻弹出，电源/音频/内存数据后台填充

## v1.2.0 (2026-06-23)

### 代码质量与安全（审查修复）
- 更新包完整性：修正过时注释，更新临时文件改用随机名避免本地抢占/TOCTOU
- `SetActivePlan` 加 5 秒超时，避免线程池线程永久挂起
- powercfg 读取改用 `BeginOutputReadLine` 异步，消除 stdout 管道死锁
- 编码改用系统 OEM 编码（`Native.GetOEMCP`），兼容非中文 Windows，失败回退 936
- 剪贴板历史用 DPAPI（CurrentUser）加密存储（文本 + 图片），平滑兼容旧版明文
- 图片去重改用 PNG 字节 SHA256，替代仅按尺寸

### 重构
- 拆分 MainWindow god class：抽出 `AppResources`（字体/资源）、`LauncherBar`（启动栏）、`WindowScaling`（缩放/定位）、`TrayController`（托盘）
- MainWindow 从 1826 行降至约 1100 行，职责单一

### 新功能
- **字体**：不再打包字体，改用系统字体；设置里下拉选系统已安装字体，实时预览
- **统一设置窗口**：标签页（常规/板块/内存/翻译/截图/剪贴板），合并删除旧设置对话框
- **固定位置**：拖到哪固定到哪，切分辨率/DPI 位置不动；仅完全离开屏幕才自动回到可视区
- **自动折叠**：鼠标离开按延时折叠，悬停展开；手动折叠后保持折叠（可设悬停也展开）
- **前台应用截图**：全局热键，CopyFromScreen 截窗口客户区，黑屏回退 Game Bar，按应用自动建子目录归档，Steam 风格右下角弹窗
- **截图图库**：独立窗口显示最近 10 张缩略图，点击定位/右键打开目录
- **剪贴板快捷键**：从鼠标位置弹出，左键复制 / 右键删除单条
- **热键占用检测**：设置时捕获后即时试注册，被占用红字提示 + 弹框

### UI 优化（借鉴 Material Design，不引 dll）
- 调色板加 elevation 层级，按钮三变体（primary 强调填充 / outline / default）
- 圆角统一，卡片阴影柔和化，悬停过渡 180ms
- 统一深色 ComboBox / TabControl / 各窗口按钮风格
- 缓存显示改"已缓存内存"（Standby + Modified，与任务管理器一致）

### 修复
- 高清渲染回退：恢复 PerMonitorV2 DPI 感知，切 4K 不再模糊
- 启动位置：首次/出界回退改右下角，加载后微调确保完整显示
- 设置窗口不弹出（深色 Tab 模板 x:Name 解析失败）
- 折叠时底部圆角
- 缓存显示 0MB（GetPerformanceInfo BAD_LENGTH，改性能计数器）
- 截图 Unknown 文件夹（改 QueryFullProcessImageName 读进程名）
- 图库空白（缩略图改 FileStream 加载中文路径；scroller 未加入 outer）

### 其他
- 日志输出到 exe 同目录 `OneBox.log`，记录截图/清理/热键/音频/电源/更新等
- 内存清理：危险项确认框、自动清理跳过危险项、Combine 默认开

## v1.1.0 (2026-06-19)
- 剪贴板支持图片（缩略图 + 点击复制回）
- 快捷启动栏拖拽（exe + .lnk，自动解析目标）
- 修复电源计划增删后音频名称消失
- 修复 LoadData 卡死（_loading 超时保护）

## v1.0.1 (2026-06-17)
- 自动更新安装 + 动态图标 + 模块化 + 启动栏/剪贴板 + 资源嵌入 + 多项修复
