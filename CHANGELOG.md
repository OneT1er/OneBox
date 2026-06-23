# 更新日志

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
