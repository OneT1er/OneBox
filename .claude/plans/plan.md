# 服务 helper 方案(OneBox 普通运行,温度/内存走服务,无 UAC)

OneBox 普通权限运行(拖放无 UIPI 限制);温度/内存由 OneBoxService(Session 0,SYSTEM)的 helper 经 Global 命名管道提供,无 UAC。

步骤:
1. TempMonitorHelper:管道改 `Global\OneBox\TempMonitor` + ACL 允许 Everyone 读(跨 session)。
2. HardwareMonitorService:非 admin 时连 Global 管道(恢复管道客户端)。
3. OneBoxService:OnStart 启动 `--temp-monitor`(Session 0 SYSTEM);LaunchInSession 改用 user token 启动**普通** OneBox(不再 LinkedToken admin)。
4. 内存清理:OneBox 非 admin 时经管道命令服务执行 CleanAll(或 --clean-memory 由服务启动)。

风险:LibreHardwareMonitor 在 Session 0 服务可能读不到 GPU(NVAPI 需用户会话),CPU/内存 MSR/SMBus 能读。需用户用 OneBoxSvc 服务自启。
