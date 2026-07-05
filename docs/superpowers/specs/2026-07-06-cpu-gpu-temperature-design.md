# CPU/GPU 温度监控 — 设计文档

**日期**: 2026-07-06  
**版本**: v1.5.0  
**状态**: 设计确认

---

## 1. 目标

在 OneBox 悬浮窗中显示 CPU 和 GPU 温度。折叠时温度显示在标题栏（始终可见），展开时显示在电源计划上方。

---

## 2. 技术方案

### 2.1 温度读取：LibreHardwareMonitorLib

- NuGet 包 `LibreHardwareMonitorLib`（.NET 8 兼容，零外部依赖）
- 直接访问硬件传感器（CPU MSR / GPU NVAPI+ADL+IGCL），覆盖 Intel/AMD/NVIDIA 全品牌
- 读取流程：`Computer.Open()` → 遍历 `Hardware[].Sensors[]` → 找 `SensorType.Temperature` → 取第一个 CPU sensor 和第一个 GPU sensor

### 2.2 替代方案（已排除）

| 方案 | 排除原因 |
|---|---|
| WMI `MSAcpi_ThermalZoneTemperature` | 台式机多返回空，无 GPU 温度 |
| NVML + ADL + MSR 分厂商对接 | 复杂度高，工作量大 |
| CoreTemp / HWiNFO 共享内存 | 需用户装第三方工具，违背单文件理念 |

---

## 3. 新增文件

### 3.1 `src/HardwareMonitorService.cs`

单例服务类：

```csharp
public class HardwareMonitorService : IDisposable
{
    public static HardwareMonitorService Instance { get; }

    public float? CpuTemperature { get; private set; }   // °C, null = 不可用
    public float? GpuTemperature { get; private set; }
    public bool IsAvailable { get; private set; }          // 硬件初始化成功

    public void Start();                                   // 创建 Computer，Open
    public void Update();                                  // 遍历 sensors，更新属性
    public void Stop();                                    // Close + Dispose
}
```

- `Update()` 耗时 <5ms，UI 线程调用安全
- 若无 CPU/GPU 温度 sensor，对应属性为 `null`，显示 `--`
- `Start()` 在后台线程执行（首次 Open 可能需 ~100-500ms 枚举硬件）

### 3.2 依赖变更

`src/OneBox.csproj` 新增：
```xml
<PackageReference Include="LibreHardwareMonitorLib" Version="*" />
```

---

## 4. UI 变更

### 4.1 展开视图 (`MainWindow.BuildUI`)

在 `_contentPanel` 最顶部、电源计划上方插入温度行：

```
_contentPanel
  ├── [NEW] _tempRow (StackPanel, Horizontal, Height=24)
  │     ├── "🌡" label
  │     ├── _cpuTempLabel: "CPU 45°C"
  │     ├── separator "·"
  │     └── _gpuTempLabel: "GPU 52°C"
  ├── [divider]
  ├── [power section]  ← 现有
  ├── ...
```

- 小字体（11px），紧凑间距
- 模块不可用时整行 `Visibility.Collapsed`
- 温度文本颜色：常温白色，高温（>80°C）橙色，超高温（>95°C）红色

### 4.2 折叠视图（标题栏）

在标题栏 `DockPanel` 的 lock 按钮左侧插入温度文本：

```
[icon] [OneBox] [CPU 45° GPU 52°] ...gap... [🔒] [▲] [✕]
```

- 小号字体（10px），灰色，不抢眼
- `DockPanel` 布局：温度文本 `HorizontalAlignment.Left`，按钮保持 `DockPanel.Dock=Right`
- 模块不可用或展开时整段隐藏（展开时 `_contentPanel` 已经显示温度，标题栏不必重复）

### 4.3 模块可见性

遵循现有 `ModuleVisible` 模式：
- `AppPrefs.Get("UI.ShowTemp", "1")` 控制可见
- `BuildUI` 中 `if (ModuleVisible("Temp"))` 包裹温度行
- 折叠栏温度同理受此开关控制

---

## 5. 设置面板

### 5.1 Modules Tab

在 Modules 复选框列表末尾新增：`☑ 温度监控`

### 5.2 温度 Tab（新增第 7 个 tab，索引 6）

| 设置项 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| 更新间隔 | 数字输入框 | 1 秒 | 范围 1-60，不合法回退默认 |
| 高温警告阈值 | 数字输入框 | 80°C | 超过后文字变橙色 |
| 超高温警告阈值 | 数字输入框 | 95°C | 超过后文字变红色 |

### 5.3 Registry 键

```
UI.ShowTemp        = "1" / "0"     (模块可见)
Temp.IntervalSec   = "1"           (更新间隔，秒)
Temp.WarnC         = "80"          (高温阈值)
Temp.CriticalC     = "95"          (超高温阈值)
```

---

## 6. 刷新机制

- **独立 Timer**：`System.Threading.Timer`，周期 = `Temp.IntervalSec` 秒（默认 1s）
- **启动/停止**：
  - `MainWindow` 构造时，若 `UI.ShowTemp == "1"`，调用 `HardwareMonitorService.Instance.Start()`
  - `MainWindow` 关闭时调用 `Stop()`
  - 窗口隐藏（`_refreshTimer` 停时）温度 timer 也暂停
- **UI 更新**：timer callback 通过 `Dispatcher.Invoke` 更新 `_cpuTempLabel.Text` / `_gpuTempLabel.Text` 和折叠栏文本

---

## 7. 错误处理

- `Start()` 失败 → `IsAvailable = false`，不抛异常，UI 显示 `--`
- 某次 `Update()` 失败 → 保留上次温度值，不更新 UI，不弹错误
- 设备休眠恢复 → `LibreHardwareMonitor` 内部自动重连传感器
- 热插拔 GPU → `Update()` 重新遍历 hardware 列表

---

## 8. 实现清单

1. 添加 `LibreHardwareMonitorLib` NuGet 包
2. 新建 `src/HardwareMonitorService.cs`
3. `MainWindow.cs`：BuildUI 中插入温度行（展开）、标题栏添加温度文本（折叠）
4. `MainWindow.cs`：独立温度刷新 timer，启动/停止逻辑
5. `SettingsDialog.cs`：Modules tab 新增复选框 + 新增温度 tab
6. `Prefs.cs`：无变更（`AppPrefs` 已支持通用 Get/Set string）
7. 冒烟测试：构建 + 启动 + 验证温度显示

---

## 9. 不做

- 温度历史图表（YAGNI，v1 只显示实时数值）
- 风扇转速、电压等其他传感器（后续可扩展）
- 温度超出阈值弹通知（后续可扩展）
- 悬浮窗外的独立温度小窗
