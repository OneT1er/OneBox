# 更新日志

## v1.8.2 (2026-08-30)

### 修复
- **异常断电后性能趋势历史丢失**：历史数据迁移到安装器管理范围之外的 `%LocalAppData%\OneT1er\OneBox\OneBox.perfhistory.json` 稳定目录；落盘改为写入临时文件、强制刷新后原子替换，并保留上一代有效备份。主文件损坏时优先恢复备份，损坏文件单独隔离，不再用坏文件覆盖好备份。
- **DIMM 温度重启后暂时消失**：DDR5 SPD 传感器冷启动尚未返回读数时保留指标并显示 `--°C`，不再把暂时缺值当成配置删除；恢复“同类型、同传感器名且唯一”时的安全重绑定，兼容 SPD 硬件名称漂移，同时拒绝有歧义的同名传感器。
- 性能历史只在用户明确删除或替换指标时清理；硬件初始化、SMBus 抖动或传感器临时失配不会再删除整条旧曲线。

## v1.8.1 (2026-08-27)

### 新增
- **安全外部截图接管**：设置 → 截图新增独立接管模式。游戏截图仍由 Game Bar、Steam、显卡工具或游戏自身响应实体快捷键完成，OneBox 只监听截图目录并负责复制归档与 Toast 提示，不模拟按键、不注入游戏进程，也不读取游戏画面。
- 接管目录支持递归监听，兼容 Steam 等按游戏建立子目录的结构；支持 PNG、JPG、JPEG、BMP、WebP 和 JXR。
- 自动等待渐进写入完成，并合并同一次 HDR 截图生成的 PNG/JXR，避免截断文件和重复提示；官方工具生成的原始文件始终保留。

### 修复
- **性能趋势前台应用一直显示 OneBox**：趋势窗口取得焦点时，改为识别其后实际使用的可见应用；加载历史时同时清理旧版本留下的无效 OneBox 记录。
- 外部截图目录与 OneBox 归档目录相同或互相包含时增加循环接管防护，避免副本被重复处理。

### 优化
- **性能指标添加选项**：按 CPU、GPU、主板、硬盘和其他硬件分类排序，显示各类型数量与传感器实时值；已添加项会标记并禁用，避免同一传感器重复添加。
- 设置界面改用线程安全的传感器快照，避免硬件采集刷新期间枚举列表产生冲突。
- 外部截图接管可在设置保存后立即启停或切换目录，无需重启 OneBox。

## v1.8.0 (2026-08-23)

### 优化
- 精简设置窗口：移除常规、板块、内存、翻译、截图和剪贴板页中重复占位的解释文字，保留必要状态、风险警告和错误提示。
- 重写 README，突出核心能力、安装入口、快捷操作、隐私边界与三进程架构。
- HTTP User-Agent 改为自动读取程序集版本，避免发版后仍携带旧版本号。

### 修复
- 修复托盘图标白色底边，并统一托盘菜单、下拉框和设置页的深色交互状态。
- 加固服务与硬件 helper 的启动、守护、依赖组合和安全管道校验，避免目录发布后缺失伴随进程或运行时依赖。

### 重构
- 迁移到 .NET 10，并拆分为 GUI、Service、Hardware 多进程目录发布；Service/Hardware 通过版本化、安全命名管道 IPC 隔离权限。
- 统一 AppCommandCatalog、入口与 typed payload，补齐统一错误、取消、重入和退出生命周期契约。
- 更新改用 Velopack 完整目录校验与应用；GUI、服务、硬件依赖按职责隔离。
- 迁移到 NAudio、H.NotifyIcon.Wpf、Hosting.WindowsServices、CommunityToolkit.Mvvm 等库；原创 app.ico/app.png 与本地矢量图标统一主题，不使用 Emoji 图标。
- 增加协议、命令、配置、生命周期、资源安全、打包与目录依赖回归测试。

## v1.7.2 (2026-08-10)

### 优化
- **悬浮窗缩放适配不同分辨率**：
  - 旧公式 `0.5+(phys-1920)*0.5/1920` 在 1080p 算出 0.5，被钳到 0.85 下限，对绝大多数用户失效
  - 新公式 `pow(diagonal/2202.9, 0.6)`，1080p=1.0
  - WPF PerMonitorV2 已按 DPI 自动缩放，LayoutTransform 不再除 DPI（旧除法在 1080p+150% DPI、4K+200% DPI 等组合下被钳到下限）
  - 4K 及以上不放大（AutoMax=1.0），仅当屏幕 < 1080p 时按对角线幂曲线缩小（小屏 1366×768≈87%，800×600≈81%）
- **Border 视觉属性同步缩放**：圆角/边线/DropShadowEffect 随 scale 同步，1080p↔4K 观感一致
- **多显示器支持**：自动按窗口所在屏幕（覆盖面积最大匹配）的分辨率计算，不再只用 PrimaryScreen
- **设置面板**显示当前 auto 缩放值（`auto 100%`）和简短说明（小屏自动缩小；手动 80%–200%）

## v1.7.1 (2026-07-31)

### 修复
- **自动更新在服务/提权实例锁 exe 时失效**：OneBoxSvc 服务与提权实例以同一 OneBox.exe 运行（LocalSystem，普通权限 taskkill 杀不掉），旧更新逻辑直接覆盖 exe 会连续失败，最终仍启动旧版本。现改为**先重命名旧 exe 再写入新文件**（运行中的 exe 允许重命名，无需管理员权限/停止服务），旧文件自动清理，失败时保留下载文件并在 %TEMP%\OneBox_update.log 记录手动修复命令
- **更新批处理延时改用 ping**：隐藏窗口下 timeout 命令会报错立即返回，失去等待进程退出的语义

## v1.7.0 (2026-07-31)

### 移除
- **自学习功能整体移除**：删除情境决策树引擎（ML.NET FastTree + k-NN 回退、观察式采样）、设置「自学习」tab、OneBox.learn.* 模型与 OneBox.samples.csv 样本逻辑；csproj 去掉 Microsoft.ML / Microsoft.ML.FastTree 依赖。电源/音频仍支持手动热键循环切换
- 移除 FeatureCollector / SampleStore / DecisionTreeLearner / LearningEngine / ForegroundWatcher 轮询部分（仅保留轻量前台 exe 捕获，供图表标注）

### 新功能
- **性能趋势图表后台持续采集**：不再需要打开图表才记录——程序运行期间即持续采样并常驻内存（全天容量固定），每 60 秒自动落盘 + 退出时落盘，崩溃最多丢 1 分钟；重启后打开图表即可看到以前记录的全部历史，时间窗自动锚到最后一条历史，新数据到达后滑回当前时间
- **历史文件防损坏**：持久化改用 unix 时间戳；历史 JSON 加载失败（损坏）时备份为 .bak 后重建，不再用空数据覆盖原文件

### 重构
- MainWindow（1806 行）拆分为 partial：UI / Data / Hotkeys / Memory / Monitor / Translate / Collapse，各模块独立文件
- SettingsDialog（1397 行）拆分为 partial：General / Modules / Memory / Translate / Screenshot / Clipboard / Metrics / Temp
- 公共样式与控件抽取到 UiKit.cs；删除无引用的 ScreenshotGallery.cs

## v1.6.3 (2026-07-25)

### 优化
- **自学习样本收集提速**：新增**观察式采样**--情境稳定时每 45 秒把「当前特征 -> 当前电源/音频」自动记一条样本（去重，不依赖手动切换）。旧版仅手动切换时记样本，要手动切 200 次才够训练，数据收集期长达 1-2 周；现在一天正常用机即可积累足够样本
- **自学习冷启动可用**：样本≥20 条即启用 **k-NN 回退预测**（exe 名命中强负偏置 + 情境距离加权投票），FastTree 模型未就绪时也能自动切换。旧版满 200 条训练前完全不自动切（1-2 周空窗）
- **训练阈值与质量**：自动训练阈值 200->50，每 +25 且距上次≥5min 重训；FastTree 5 树 10 叶->30 树 24 叶。`Predict` 走 FastTree 优先、某目标无模型时回退 k-NN
- **FastTree 加入 exe 名特征**：FastTree 特征向量增加前台 exe 名的 one-hot 编码，让模型能区分"同类别不同应用"的偏好（如不同游戏分别外放/耳机）。旧版 FastTree 只看进程类别（Game/Creative/...），同类下不同游戏无法区分；k-NN 本就用 exe 名做强信号，现在 FastTree 也跟上
- **性能**：`SampleStore.Count` 加缓存（Append 自增/Clear 清零），避免推理/训练门每秒读整个 CSV

### 新功能
- **性能趋势图表时长档扩充**：5分/15分/30分/1时/2时/6时/12时/全天，默认 15 分（原仅 15分/1时/全天）

### 修复
- **性能图表在无数据处填旧值**：`HardwareMonitorService` 传感器读数失败时用 `_lastMetrics` 兜底供 UI 显示（避免闪烁），但旧版把兜底旧值也存入 `PerfHistory`，导致图表在没采到数据的地方填上一次的值。现 `MetricValue` 加 `Cached` 标记，`PerfHistory` 仅存真实读数；`PerfChart` 改为按时间戳映射 x，相邻点时间差超阈值（3 间隔/≥5s）断线--传感器失配/跨重启缺口显示为断口而非填旧值。`PerfHistory` 持久化时间戳，旧 JSON 无时间戳时按文件修改时间回填

### 重构
- 移除死代码：`LearningEngine.Enabled`、`DecisionTreeLearner.Unload()`、`FeatureCollector.IsRunning/IntervalMs` 公共成员、`DevicePrefs.SetHotkey` 别名、MainWindow 三个未用字段（`_perfChart`/`_perfChartPanel`/`_dragDropWired`，消除 CS0169/CS0649 警告）
- 方法改名 `StartAppProfile/RestartAppProfile` -> `StartLearning/RestartLearning`（AppProfile 是已删旧概念）
- 设置面板状态文案区分「k-NN 回退预测中 / 已训练」，说明文案补充观察式采样与新阈值

## v1.6.2 (2026-07-19)

### 修复
- **自学习「立即训练」无效**：单文件发布未把 ML.NET 的 native 库（`FastTreeNative.dll` 等）打包进 exe，而自动更新只搬 OneBox.exe 一个文件，导致用户机器上 FastTree 找不到 `FastTreeNative.dll`，训练每次 `train fail: 0x8007007E`、模型建不出来（日志累计 54 次失败）。csproj 改 `IncludeNativeLibrariesForSelfExtract=true`，native 库打进单文件、启动解压到 `%TEMP%`，exe +1.8MB。现有样本（≥30 条）点「立即训练」即可正常生成模型

## v1.6.1 (2026-07-16)

### 重构
- **自学习改为情境决策树（ML.NET）**：替代旧「按应用投票」。`FeatureCollector` 每 1s 采集 CPU/GPU 占用、全屏、电池、时间、进程类别（game/creative/videoconf/other 白名单 + 自定义）等情境特征；手动切换电源/音频时记样本到 `OneBox.samples.csv`；样本达 200 条自动训练（ML.NET FastTree `OneVersusAll` 多分类，电源/音频各一 .zip 模型，80/20 验证准确率）；推理连续 5s 稳定才套用，切后冷却 30s，手动切换暂停 10min 并记新样本。设置->自学习 tab 显示样本数/准确率/手动训练/重置/清空 + 自定义游戏进程。CPU 走原生 `GetSystemTimes` 差分，GPU 走性能计数器求和

### 改进
- **开机自启**：改为勾选框，写用户 flag（`AutoStart.Enabled`）无需 UAC；服务启动 GUI 前 impersonate 读用户 HKCU，取消勾选即不再自动启动
- **温度 helper 守护**：服务守护 helper 进程，崩溃 3s 自动重启、OnStop 清理；helper 不再因 60s 无客户端退出

### 修复
- **重启后性能监控无数据**：温度 helper 旧版 60s 无客户端退出，GUI 关闭后死亡，重启时 `[Temp] pipe connect fail` 无数据。改为无限等待 + 服务守护
- **自学习设置文本显示不全**：精简勾选框文案，详情（5s 稳定/30s 冷却/10min 暂停）移入换行说明
- **退出时历史保存被跳过**：`ExitApp` 拆分独立 try/catch，避免 `LearningEngine.Stop()` 抛异常时 `PerfHistory.Save()` 被跳过

## v1.6.0 (2026-07-15)

### 新功能
- **自学习（前台应用自动切换）**：切到某应用自动套用其电源计划 + 音频输出；投票统计学习手动切换（手动 +10 票/自动 +1），避免偶发手动被旧习惯覆盖；切换右下角弹窗（可关闭）；设置->自学习 tab 管理规则（编辑/禁用/删除/锁定）
- **性能趋势图表**：悬浮窗入口 + 双击大图（15min/1h/全天）；双 Y 轴（温度+风扇）；鼠标 tooltip（十字线+各线值+该时间点前台应用）；前台应用时间段色块；历史持久化 JSON 跨重启
- **服务 helper（无 UAC）**：OneBox 普通运行（拖放无 UIPI），温度/内存由 OneBoxSvc 服务（SYSTEM）经命名管道 IPC 提供

### 改进
- **拖放**：Window 级 AllowDrop + UIPI 消息过滤；LauncherBar 点击菜单（程序/文件夹/网页）；浏览器 URL 拖入 + favicon 自动获取；折叠态拖入自动展开
- **开机自启**：简化为固定服务，设置/托盘"关闭开机自启"请求 UAC 删服务
- **设置清理**：删图表开关、内存 admin 横幅等冗余；自学习独立 tab

### 修复
- **DDR5 内存温度**：FindSensor 三级 fallback（精确->SensorName->内存类兜底）解决 SPD 名字乱码漂移；过滤元数据传感器；读不到保留上次值
- **温度服务 helper**：非 admin 连 Global 管道；helper 推送所有传感器，OneBox 用用户 EnabledMetrics 过滤；设置预览/传感器列表经管道
- **风扇预览**：匹配加 SensorType 区分 RPM 与 %
- **图表删指标旧数据**：PerfHistory 清理
- **重启指标丢失**：非 admin Start 调 LoadEnabledMetrics

## v1.4.3 (2026-07-01)

### 修复
- **开机自启服务零 UAC 提权**：服务通过 `TokenLinkedToken` 获取用户的管理员令牌，`CreateProcessAsUser` 直接以管理员权限启动 GUI，无需 UAC 弹窗
- **服务主动扫描会话**：`OnStart` 中枚举已登录会话并启动 GUI，解决服务重启后不触发 `OnSessionChange` 的问题
- **修复 `lpDesktop` 导致 0xC0000142**：移除 `STARTUPINFO.lpDesktop = "winsta0\default"`，该设置导致所有通过服务启动的进程因 `STATUS_DLL_INIT_FAILED` 崩溃
- **退出码诊断**：`LaunchWithToken` 等待 8 秒检测进程退出码，便于定位启动失败原因
- **`EnableService` 错误传播**：`sc.Start()` 失败时返回错误而非假成功
- **托盘禁用失败回滚**：`Disable()` 失败时弹框报错并回滚勾选状态
- **Main 早期日志**：进程启动第一行即记录，用于诊断开机自启中 GUI 静默消失

### 变更
- 选服务/计划任务自启后若当前非管理员则立即提权重启

## v1.4.2 Hotfix (2026-06-30)

### 修复
- **开机自启检测**：`ServiceController` 构造函数对不存在的服务不抛异常，改为读 `Status` 属性验证
- **提权重启**：`RestartAsAdmin` 启动新实例前释放单实例 Mutex/EventWaitHandle
- **自启切换**：`DisableAll` 返回错误不再静默；清理失败时拒绝启用新方式，下拉框回滚
- **UAC 品牌**：需要管理员权限时启动 `OneBox.exe --elevate-autostart` 提权 helper，UAC 对话框显示 OneBox 而非系统程序名

## v1.4.1 (2026-06-30)

### 修复
- **开机自启检测**：`ServiceController` 构造函数对不存在的服务不抛异常，改为读 `Status` 属性验证
- **提权重启**：`RestartAsAdmin` 启动新实例前释放单实例 Mutex/EventWaitHandle

## v1.4.0 (2026-06-30)

### 新功能
- **启动栏扩展**：4 格 → 8 格，WrapPanel 自动换行；支持拖入 URL 链接和文件夹路径；URL 自动获取网站 favicon；空位仅显示已填数 + 1
- **三种开机自启方式**：注册表（普通）/ 计划任务（管理员）/ Windows 服务（SYSTEM），设置中切换，托盘一键开关
- **窗口缩放**：设置 → 常规滑块 80%~200%，勾"自动"根据屏幕分辨率自适应
- **单实例激活**：重复启动不再默默退出，激活已有窗口置顶

### 修复
- **截图 DPI**：PerMonitorV2 下截 1080p 变 2880px 的问题，自动检测窗口 DPI 缩回逻辑分辨率
- **单实例**：改用 Mutex.TryOpenExisting 消除竞态，杜绝多实例共存
- **UI 风格统一**：托盘开机自启改为简洁勾选开关，与顶置/锁定位置一致
- 启动栏存储改为紧凑列表，右键清除直接压缩无需预留空串

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








