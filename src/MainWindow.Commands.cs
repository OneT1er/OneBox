using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using OneBox.Contracts;
using PowerAudioManager.Commands;

namespace PowerAudioManager
{
    public partial class MainWindow
    {
        IAppCommandDispatcher _commandDispatcher;

        public IAppCommandDispatcher CommandDispatcher => _commandDispatcher;

        void InitializeCommands()
        {
            _commandDispatcher = new AppCommandDispatcher(ExecuteCommandCoreAsync,
                (term, error) => AppLog.Log("Command " + term, error));
        }

        internal AsyncRelayCommand CreateUiCommand(AppCommandId id, CommandSource source,
            Func<object> payloadFactory = null)
        {
            return new AsyncRelayCommand(() => ExecuteCommandAsync(id, source, payloadFactory?.Invoke()));
        }

        internal async Task<CommandResult> ExecuteCommandAsync(AppCommandId id, CommandSource source,
            object payload = null, System.Threading.CancellationToken cancellationToken = default)
        {
            var result = await _commandDispatcher.DispatchAsync(
                new CommandRequest(id, source, payload, cancellationToken));
            AppLog.Log("Command", $"{source}/{id}: {(result.Success ? "ok" : result.ErrorCode.ToString())}");
            if (!result.IsCancelled && !string.IsNullOrWhiteSpace(result.UserMessage))
            {
                MessageBox.Show(this, result.UserMessage, AppCommandCatalog.TryGet(id, out var definition)
                    ? definition.Term : "OneBox", MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            return result;
        }

        async Task<CommandResult> ExecuteCommandCoreAsync(CommandRequest request)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            switch (request.CommandId)
            {
                case AppCommandId.WindowShow:
                    ShowWindow();
                    return CommandResult.Ok();
                case AppCommandId.WindowHide:
                    Hide();
                    return CommandResult.Ok();
                case AppCommandId.WindowSetCollapsed:
                    SetExpanded(!request.RequirePayload<WindowCollapsedPayload>().Collapsed, true);
                    return CommandResult.Ok();
                case AppCommandId.AppExit:
                    ExitApp();
                    return CommandResult.Ok();
                case AppCommandId.SettingsOpen:
                    ShowWindow();
                    SettingsDialog.Show(this, request.RequirePayload<SettingsOpenPayload>().TabIndex);
                    return CommandResult.Ok();
                case AppCommandId.PowerList:
                    return CommandResult.Ok(await Task.Run(PowerPlanService.GetPowerPlans,
                        request.CancellationToken));
                case AppCommandId.PowerActivate:
                    return await ActivatePowerPlanAsync(request.RequirePayload<PowerActivatePayload>().PlanId,
                        request.CancellationToken);
                case AppCommandId.PowerCycle:
                    return await CyclePowerPlanAsync(request.CancellationToken);
                case AppCommandId.AudioList:
                    return CommandResult.Ok(AudioDevices.GetOutputDevices());
                case AppCommandId.AudioActivate:
                    return ActivateAudioOutput(request.RequirePayload<AudioActivatePayload>().DeviceId);
                case AppCommandId.AudioCycle:
                    return CycleAudioOutput();
                case AppCommandId.AudioSetVolume:
                    return SetAudioVolume(request.RequirePayload<AudioVolumePayload>().Volume);
                case AppCommandId.AudioSetMute:
                    return SetAudioMute(request.RequirePayload<AudioMutePayload>().Muted);
                case AppCommandId.MemoryClean:
                    return await CleanMemoryCoreAsync(request.RequirePayload<MemoryCleanPayload>().Flags,
                        request.CancellationToken);
                case AppCommandId.TranslateText:
                    return await ExecuteTextTranslateAsync(request.RequirePayload<TextTranslatePayload>().Text);
                case AppCommandId.TranslateImageRegion:
                    return await TranslateImageRegionAsync(request.CancellationToken);
                case AppCommandId.TranslateImageClipboard:
                    return await TranslateClipboardImageAsync(request.CancellationToken);
                case AppCommandId.ScreenshotForeground:
                    await ScreenshotService.CaptureForegroundAsync(request.CancellationToken);
                    return CommandResult.Ok();
                case AppCommandId.ScreenshotOpenGallery:
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + ScreenshotService.RootDir() + "\"")
                        { UseShellExecute = true });
                    return CommandResult.Ok();
                case AppCommandId.ClipboardOpen:
                    var location = request.RequirePayload<ClipboardOpenPayload>();
                    if (location.ScreenX.HasValue && location.ScreenY.HasValue)
                        ClipboardHistoryPanel.ShowAt(this, location.ScreenX.Value, location.ScreenY.Value);
                    else ClipboardHistoryPanel.Show(this);
                    return CommandResult.Ok();
                case AppCommandId.ClipboardClear:
                    ClipboardHistory.Clear();
                    return CommandResult.Ok(userMessage: "剪贴板历史已清空。");
                case AppCommandId.LauncherShow:
                    ShowWindow();
                    return CommandResult.Ok();
                case AppCommandId.LauncherAdd:
                    return LauncherBar.AddPaths(request.RequirePayload<LauncherAddPayload>().Paths, RebuildUI);
                case AppCommandId.LauncherRemove:
                    return LauncherBar.RemoveAt(request.RequirePayload<LauncherRemovePayload>().SlotIndex, RebuildUI);
                case AppCommandId.LauncherLaunch:
                    return LauncherBar.LaunchAt(request.RequirePayload<LauncherLaunchPayload>().SlotIndex);
                case AppCommandId.MonitorChartOpen:
                    new PerfChartWindow().Show();
                    return CommandResult.Ok();
                case AppCommandId.MonitorStart:
                    RestartTempTimer();
                    return CommandResult.Ok();
                case AppCommandId.MonitorStop:
                    StopTempMonitor();
                    return CommandResult.Ok();
                case AppCommandId.UpdateCheck:
                    UpdateCheckPayload updatePayload = request.RequirePayload<UpdateCheckPayload>();
                    UpdateOperationResult updateResult = await UpdateCommandBridge.ExecuteAsync(
                        token => UpdateChecker.CheckAsync(this, updatePayload.Manual, token),
                        request.CancellationToken);
                    if (!updateResult.Success)
                    {
                        if (updateResult.ErrorCode == UpdateErrorCode.Cancelled) return CommandResult.Cancelled();
                        CommandErrorCode commandError = updateResult.ErrorCode switch
                        {
                            UpdateErrorCode.NotInstalled => CommandErrorCode.NotAvailable,
                            UpdateErrorCode.LockConflict => CommandErrorCode.Busy,
                            UpdateErrorCode.CoordinationFailed => CommandErrorCode.Rejected,
                            _ => CommandErrorCode.Failed,
                        };
                        return CommandResult.Fail(commandError, updateResult.Message, updateResult);
                    }
                    return CommandResult.Ok(updateResult,
                        updatePayload.Manual ? updateResult.Message : string.Empty);
                case AppCommandId.AutoStartApply:
                    return ApplyAutoStart(request.RequirePayload<AutoStartApplyPayload>());
                case AppCommandId.RuntimeRefreshHotkeys:
                    RefreshHotkeys();
                    return CommandResult.Ok();
                case AppCommandId.RuntimeRestartAutoClean:
                    RestartAutoCleanTimer();
                    return CommandResult.Ok();
                case AppCommandId.RuntimeApplyGeneral:
                    return ApplyGeneralRuntime(request.RequirePayload<GeneralRuntimePayload>());
                case AppCommandId.RuntimeRebuildModules:
                    RebuildUI();
                    RefreshHotkeys();
                    return CommandResult.Ok();
                default:
                    return CommandResult.Fail(CommandErrorCode.UnknownCommand, "不支持的功能指令。");
            }
        }

        async Task<CommandResult> ActivatePowerPlanAsync(string planId,
            System.Threading.CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(planId))
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "电源计划标识不能为空。");
            bool success = await Task.Run(() => PowerPlanService.SetActivePlan(planId), cancellationToken);
            if (!success) return CommandResult.Fail(CommandErrorCode.Rejected, "切换电源计划失败。");
            _currentPlanId = planId;
            LoadData();
            return CommandResult.Ok();
        }

        async Task<CommandResult> CyclePowerPlanAsync(System.Threading.CancellationToken cancellationToken)
        {
            var plans = await Task.Run(PowerPlanService.GetPowerPlans, cancellationToken);
            if (plans == null || plans.Count == 0)
                return CommandResult.Fail(CommandErrorCode.NotAvailable, "没有可用的电源计划。");
            int current = plans.FindIndex(plan => plan.IsActive);
            var target = plans[current < 0 ? 0 : (current + 1) % plans.Count];
            bool success = await Task.Run(() => PowerPlanService.SetActivePlan(target.Guid), cancellationToken);
            if (!success) return CommandResult.Fail(CommandErrorCode.Rejected, "切换电源计划失败。");
            LoadData();
            AppProfileToast.ShowPowerSwitch(target.Name);
            return CommandResult.Ok(target);
        }

        CommandResult ActivateAudioOutput(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "音频输出标识不能为空。");
            if (!AudioDevices.SetDefaultDevice(deviceId))
                return CommandResult.Fail(CommandErrorCode.Rejected, "切换音频输出失败。");
            _currentDeviceId = deviceId;
            VolumeControl.Invalidate();
            LoadData();
            ScheduleVolumeRefresh();
            return CommandResult.Ok();
        }

        CommandResult CycleAudioOutput()
        {
            var devices = AudioDevices.GetOutputDevices().FindAll(device => !device.IsHidden);
            if (devices.Count == 0)
                return CommandResult.Fail(CommandErrorCode.NotAvailable, "没有可用的音频输出。");
            int current = devices.FindIndex(device => device.IsDefault);
            var target = devices[current < 0 ? 0 : (current + 1) % devices.Count];
            var result = ActivateAudioOutput(target.Id);
            if (result.Success) AppProfileToast.ShowAudioSwitch(target.Name);
            return result.Success ? CommandResult.Ok(target) : result;
        }

        CommandResult SetAudioVolume(float volume)
        {
            if (float.IsNaN(volume) || volume < 0 || volume > 1)
                return CommandResult.Fail(CommandErrorCode.InvalidPayload, "音量必须在 0 到 100% 之间。");
            VolumeControl.SetVolume(volume);
            UpdateVolumeUI();
            return CommandResult.Ok();
        }

        CommandResult SetAudioMute(bool muted)
        {
            VolumeControl.SetMute(muted);
            UpdateVolumeUI();
            return CommandResult.Ok();
        }

        async Task RunStartupUpdateCheckAsync()
        {
            try { await UpdateChecker.CheckAsync(this, false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { AppLog.Log("Startup update check", ex); }
        }

        async Task<CommandResult> ExecuteTextTranslateAsync(string text)
        {
            OpenTranslateWindow(null);
            if (!string.IsNullOrEmpty(text)) await _translateWindow.RunTranslationAsync(text);
            return CommandResult.Ok();
        }

        CommandResult ApplyAutoStart(AutoStartApplyPayload payload)
        {
            string error = payload.Enabled
                ? AutoStartService.ApplyAutoStart((AutoStartMethod)AutoStartSettingsDecision.Create(true,
                    payload.PreferredMethod).Method)
                : AutoStartService.Disable();
            if (error != null) return CommandResult.Fail(CommandErrorCode.Failed, error);
            _tray?.UpdateAutoStart();
            return CommandResult.Ok();
        }

        CommandResult ApplyGeneralRuntime(GeneralRuntimePayload payload)
        {
            bool previousTopmost = _topmost;
            bool previousLockPosition = _lockPosition;
            if (!AppPrefs.Set(PreferenceKeys.Window.Topmost, payload.Topmost))
                return CommandResult.Fail(CommandErrorCode.Failed, "窗口设置保存失败，运行状态未应用。\n请检查用户注册表权限后重试。");
            if (!AppPrefs.Set(PreferenceKeys.Window.LockPosition, payload.LockPosition))
            {
                // Keep persistence and live state aligned when the second
                // registry write fails after the first one succeeded.
                AppPrefs.Set(PreferenceKeys.Window.Topmost, previousTopmost);
                return CommandResult.Fail(CommandErrorCode.Failed, "窗口设置保存失败，运行状态未应用。\n请检查用户注册表权限后重试。");
            }
            _topmost = payload.Topmost;
            Topmost = payload.Topmost;
            _lockPosition = payload.LockPosition;
            _tray?.SetLockChecked(payload.LockPosition);
            if (_pinBtn != null)
            {
                _pinBtn.Content = UiKit.PinIcon(payload.LockPosition, UiKit.FrozenBrush(payload.LockPosition ? UiKit.AccentColor : UiKit.TextSecondary));
                _pinBtn.Foreground = new System.Windows.Media.SolidColorBrush(
                    payload.LockPosition ? UiKit.AccentColor : UiKit.TextSecondary);
                System.Windows.Automation.AutomationProperties.SetName(_pinBtn,
                    payload.LockPosition ? "解除锁定窗口位置" : "锁定窗口位置");
            }
            if (payload.ResetScale) _scaling?.ResetManualScale();
            else if (payload.ManualScale.HasValue) _scaling?.ApplyManualScale(payload.ManualScale.Value);
            RefreshAutoCollapse();
            RefreshHotkeys();
            if (payload.RefreshFont) ApplyFont();
            return CommandResult.Ok();
        }
    }
}
