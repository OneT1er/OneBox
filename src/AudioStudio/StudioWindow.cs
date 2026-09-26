using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PowerAudioManager.Commands;

namespace PowerAudioManager.AudioStudio;

internal sealed class StudioWindow
{
    readonly MainWindow _owner;
    readonly StudioController _controller;
    readonly Window _window;
    readonly StackPanel _body = new() { Margin = new Thickness(18) };
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<(CheckBox Box, Func<StudioSettings, bool> Read)> _toggles = new();
    ComboBox _input, _output, _apps, _monitor;
    ComboBox _voicePreset;
    string[] _presetLabels;
    TextBlock _status, _levels;
    Button _start;
    StudioSpectrum _spectrum;
    StudioMeter _inputMeter, _outputMeter;
    bool _building, _refreshing;
    int _ticks;
    bool Light => _controller.Settings.Theme == "light";
    Brush Foreground => Light ? new SolidColorBrush(Color.FromRgb(43, 39, 60)) : ThemeTokens.Brush(ThemeTokens.PrimaryText);
    Brush Muted => Light ? new SolidColorBrush(Color.FromRgb(99, 91, 112)) : ThemeTokens.Brush(ThemeTokens.SecondaryText);
    string T(string zh, string en) => _controller.T(zh, en);
    public event EventHandler Closed;
    public StudioWindow(MainWindow owner, StudioController controller)
    {
        _owner = owner; _controller = controller;
        var scroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _window = OneBoxWindow.Create(owner, T("麦克风工作室", "Microphone Studio"), 660, 830, scroll, true);
        _window.MinWidth = 560; _window.MinHeight = 500;
        _window.Closed += (_, _) => { _timer.Stop(); _controller.Changed -= Sync; Closed?.Invoke(this, EventArgs.Empty); };
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized) { _window.Hide(); _window.WindowState = WindowState.Normal; } };
        _controller.Changed += Sync;
        Build();
        _timer.Tick += async (_, _) =>
        {
            if (!_window.IsVisible) return;
            Sync();
            if (_window.IsVisible && ++_ticks % 38 == 0) await RefreshDevices();
        };
        _timer.Start();
    }
    public void Show() => _window.Show();
    public void Activate() => _window.Activate();
    public void Close() => _window.Close();
    TextBlock Text(string text, double size = 12) => new() { Text = text, FontSize = size, Foreground = Foreground, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) };
    Button Button(string title, Action action)
    {
        var b = new Button { Content = title, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 2, 8, 2), Foreground = Foreground };
        UiKit.ApplyFlatStyle(b); b.Click += (_, _) => Guard(action); return b;
    }
    void Guard(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(_window, ex.Message, "OneBox"); } }
    void Change(Action<StudioSettings> edit, bool restart = false)
    {
        if (_building) return;
        Guard(() => { var s = _controller.Settings.Copy(); edit(s); _controller.Apply(s); if (restart && _controller.Wanted) _ = _controller.StartAsync(); });
    }
    StackPanel Card(string title, string subtitle = null)
    {
        var panel = new StackPanel(); var heading = Text(title, 15); heading.FontWeight = FontWeights.SemiBold; panel.Children.Add(heading);
        if (subtitle != null) { var t = Text(subtitle); t.Foreground = Muted; panel.Children.Add(t); }
        _body.Children.Add(new Border { Background = Light ? Brushes.White : ThemeTokens.Brush(ThemeTokens.Card), CornerRadius = new CornerRadius(10), Padding = new Thickness(15), Margin = new Thickness(0, 0, 0, 12), Child = panel });
        return panel;
    }
    ComboBox Combo(Panel parent, string label, IEnumerable<object> items, object selected, Action<object> changed)
    {
        parent.Children.Add(Text(label));
        var combo = new ComboBox { ItemsSource = items, SelectedItem = selected, MinHeight = 30, Margin = new Thickness(0, 0, 0, 8) };
        combo.Style = ThemeTokens.CreateDarkComboBoxStyle(Light);
        combo.SelectionChanged += (_, _) => { if (!_building && !_refreshing && combo.SelectedItem != null) changed(combo.SelectedItem); };
        parent.Children.Add(combo); return combo;
    }
    void Toggle(Panel parent, string text, Func<StudioSettings, bool> read, Action<StudioSettings, bool> write, bool restart = false)
    {
        var box = new CheckBox { Content = text, IsChecked = read(_controller.Settings), Foreground = Foreground, Margin = new Thickness(0, 7, 0, 7) };
        box.Click += (_, _) => Change(s => write(s, box.IsChecked == true), restart);
        _toggles.Add((box, read)); parent.Children.Add(box);
    }
    Slider Slider(Panel parent, string label, double value, double max, Action<float> changed, double min = 0)
    {
        var caption = Text(label + "  " + Math.Round(value) + "%"); parent.Children.Add(caption);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = 1, Foreground = ThemeTokens.Brush(ThemeTokens.Accent), Margin = new Thickness(0, 0, 0, 8) };
        slider.ValueChanged += (_, _) => { caption.Text = label + "  " + Math.Round(slider.Value) + "%"; if (!_building) changed((float)slider.Value); };
        parent.Children.Add(slider); return slider;
    }
    void ApplyShell(Window window)
    {
        if (window.Content is Border border)
        {
            border.Background = Light ? new SolidColorBrush(Color.FromRgb(247, 244, 240)) : ThemeTokens.Brush(ThemeTokens.Background);
            if (border.Child is Grid grid && grid.Children[0] is Border title)
            {
                title.Background = Light ? new SolidColorBrush(Color.FromRgb(234, 229, 239)) : ThemeTokens.Brush(ThemeTokens.TitleSurface);
                if (title.Child is DockPanel dock) foreach (var child in dock.Children)
                {
                    if (child is TextBlock text) text.Foreground = Foreground;
                    if (child is Button close) { close.ToolTip = T("关闭", "Close"); System.Windows.Automation.AutomationProperties.SetName(close, T("关闭", "Close")); }
                }
            }
        }
    }
    void Build()
    {
        _building = true; _body.Children.Clear(); _toggles.Clear(); ApplyShell(_window);
        _window.Title = T("麦克风工作室", "Microphone Studio");
        if (_window.Content is Border shell && shell.Child is Grid shellGrid && shellGrid.Children[0] is Border titleBar && titleBar.Child is DockPanel titleDock)
            foreach (var child in titleDock.Children) if (child is TextBlock title) title.Text = "  " + _window.Title;
        var s = _controller.Settings;
        var toolbar = new WrapPanel();
        toolbar.Children.Add(Button(T("设置", "Settings"), Settings));
        toolbar.Children.Add(Button(T("均衡器", "Equalizer"), Equalizer));
        toolbar.Children.Add(Button(T("使用教程", "Guide"), Guide));
        toolbar.Children.Add(Button(T("收起到托盘", "Hide to tray"), () => _window.Hide()));
        _body.Children.Add(toolbar);
        var state = Card(T("把音乐和你的声音分享给朋友", "Share your voice and music"), T("选好设备 → 开始共享 → 通话软件选择 CABLE Output", "Choose devices → Start sharing → select CABLE Output in your chat app"));
        _status = Text(""); state.Children.Add(_status);
        _start = Button("", () => { if (_controller.Wanted) _ = _controller.StopAsync(); else _ = _controller.StartAsync(); });
        _start.Background = ThemeTokens.Brush(ThemeTokens.Accent); _start.Foreground = Brushes.White;
        _start.FontWeight = FontWeights.SemiBold; state.Children.Add(_start);
        _levels = Text(""); state.Children.Add(_levels);
        _inputMeter = new StudioMeter { Height = 5, Margin = new Thickness(0, 0, 0, 4), ToolTip = T("麦克风电平", "Microphone level") };
        _outputMeter = new StudioMeter { Height = 5, Margin = new Thickness(0, 0, 0, 8), ToolTip = T("输出电平", "Output level") };
        state.Children.Add(_inputMeter); state.Children.Add(_outputMeter);
        _spectrum = new StudioSpectrum(); state.Children.Add(_spectrum);
        var devices = Card(T("1  连接设备", "1  Connect devices"));
        _input = Combo(devices, T("你的麦克风", "Your microphone"), Array.Empty<object>(), null, item => Change(x => x.InputId = ((StudioDevice)item).Id, true));
        _output = Combo(devices, T("分享给朋友 · 虚拟输出", "Share with friends · virtual output"), Array.Empty<object>(), null, item => Change(x => x.OutputId = ((StudioDevice)item).Id, true));
        var deviceGrid = new Grid(); deviceGrid.ColumnDefinitions.Add(new ColumnDefinition()); deviceGrid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int col = 0; col < 2; col++)
        {
            var column = new StackPanel { Margin = new Thickness(col == 0 ? 0 : 8, 0, col == 0 ? 8 : 0, 0) };
            for (int row = 0; row < 2; row++) { var element = devices.Children[1]; devices.Children.RemoveAt(1); column.Children.Add(element); }
            Grid.SetColumn(column, col); deviceGrid.Children.Add(column);
        }
        devices.Children.Add(deviceGrid);
        devices.Children.Add(Button(T("安装 VB-CABLE（官方）", "Install VB-CABLE (official)"), () => Open("https://vb-audio.com/Cable/")));
        var voice = Card(T("2  调整人声", "2  Adjust your voice"));
        Toggle(voice, T("混入麦克风", "Include microphone"), x => x.Microphone, (x, v) => x.Microphone = v, true);
        var presets = new[] { T("安静", "Quiet"), T("标准", "Standard"), T("嘈杂", "Noisy"), T("直播", "Live"), T("自定义", "Custom") };
        _presetLabels = presets;
        var keys = new[] { "Quiet", "Standard", "Noisy", "Live", "Custom" };
        _voicePreset = Combo(voice, T("场景预设", "Voice preset"), presets, presets[Math.Max(0, Array.IndexOf(keys, s.Preset))], item =>
        { Change(x => { string key = keys[Array.IndexOf(presets, (string)item)]; if (key == "Custom") x.Preset = key; else x.ApplyPreset(key); }, true); Build(); });
        Combo(voice, T("降噪模型", "Denoising model"), StudioDenoisers.Factories.Keys, s.Model, item => Change(x => { x.Model = (string)item; x.Preset = "Custom"; }, true));
        Toggle(voice, T("实时降噪", "Real-time denoising"), x => x.Denoise, (x, v) => x.Denoise = v);
        Slider(voice, T("降噪强度", "Noise reduction"), s.Strength * 100, 100, value => Change(x => { x.Strength = value / 100; x.Preset = "Custom"; }));
        Slider(voice, T("人声音量", "Voice volume"), s.MicGain * 100, 300, value => Change(x => { x.MicGain = value / 100; x.Preset = "Custom"; }));
        Toggle(voice, T("启用 10 段均衡器", "Enable 10-band EQ"), x => x.Eq, (x, v) => x.Eq = v);
        CollapseCard(voice, T("2  人声处理 · 降噪 / 音量 / EQ", "2  Voice · denoise / volume / EQ"));
        var music = Card(T("3  选择音乐软件", "3  Choose a music app"), T("只分享所选应用。你仍通过播放器正常听音乐。", "Only the selected app is shared. Keep listening through your player as usual."));
        _apps = Combo(music, T("先播放音乐，再选择应用", "Play music, then select the app"), Array.Empty<object>(), null, item => Change(x => x.ApplicationPath = ((StudioApplication)item).Path, true));
        Toggle(music, T("分享应用音乐", "Share application music"), x => x.Music, (x, v) => x.Music = v, true);
        Slider(music, T("朋友听到的音乐音量", "Music volume for friends"), s.MusicGain * 100, 200, value => Change(x => x.MusicGain = value / 100));
        var advanced = Card(T("监听与音效", "Monitoring and effects"), T("日常听音乐无需开启监听；最终输出包含你的人声和音乐。", "Monitoring is optional; Final output includes your voice and music."));
        _monitor = Combo(advanced, T("监听耳机", "Monitor headphones"), Array.Empty<object>(), null, item => Change(x => x.MonitorId = ((StudioDevice)item).Id, true));
        string[] points = { T("原始输入", "Raw input"), T("降噪后", "After denoise"), T("增益后", "After gain"), T("EQ 后", "After EQ"), T("最终输出", "Final output") };
        Combo(advanced, T("监听位置", "Monitor point"), points, points[s.MonitorPoint], item => Change(x => x.MonitorPoint = Array.IndexOf(points, (string)item)));
        Toggle(advanced, T("开启监听", "Enable monitoring"), x => x.Monitor, (x, v) => x.Monitor = v, true);
        Toggle(advanced, T("炸麦音效", "Distortion effect"), x => x.Explode, (x, v) => x.Explode = v);
        Slider(advanced, T("炸麦强度", "Distortion intensity"), s.ExplodeStrength * 100, 100, value => Change(x => x.ExplodeStrength = value / 100), 1);
        CollapseCard(advanced, T("监听与炸麦音效", "Monitoring and distortion"));
        _building = false; Sync(); _ = RefreshDevices();
    }
    void CollapseCard(StackPanel panel, string title)
    {
        if (panel.Parent is not Border border) return;
        panel.Children.RemoveAt(0); border.Child = null;
        border.Child = new Expander { Header = title, Foreground = Foreground, FontSize = 14, Content = panel, IsExpanded = false };
    }
    async Task RefreshDevices()
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            var devices = await Task.Run(() => (StudioDevices.List(DataFlow.Capture), StudioDevices.List(DataFlow.Render), StudioDevices.Applications()));
            if (_window.Dispatcher.HasShutdownStarted) return;
            var s = _controller.Settings;
            Set(_input, devices.Item1, s.InputId); Set(_output, devices.Item2.Where(x => x.IsCable).ToArray(), s.OutputId);
            Set(_monitor, devices.Item2.Where(x => !x.IsCable).ToArray(), s.MonitorId);
            if (!_apps.IsDropDownOpen)
            {
                _apps.ItemsSource = devices.Item3;
                _apps.SelectedItem = devices.Item3.FirstOrDefault(x => string.Equals(x.Path, s.ApplicationPath, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) { _status.Text = ex.Message; }
        finally { _refreshing = false; }
        void Set(ComboBox box, StudioDevice[] values, string id)
        {
            if (box.IsDropDownOpen) return;
            box.ItemsSource = values; box.SelectedItem = values.FirstOrDefault(x => x.Id == id);
        }
    }
    void Sync()
    {
        if (_status == null) return;
        string status = _controller.Status;
        int separator = status.IndexOf(" / ", StringComparison.Ordinal);
        _status.Text = separator < 0 ? status : _controller.Settings.Language == "en" ? status.Substring(separator + 3) : status.Substring(0, separator);
        _start.Content = _controller.Wanted ? T("停止共享", "Stop sharing") : T("开始共享", "Start sharing");
        _start.IsEnabled = true;
        foreach (var (box, read) in _toggles) box.IsChecked = read(_controller.Settings);
        if (_voicePreset != null && _controller.Settings.Preset == "Custom" && !_voicePreset.IsDropDownOpen)
        {
            bool building = _building; _building = true; _voicePreset.SelectedItem = _presetLabels[4]; _building = building;
        }
        var e = _controller.Engine;
        _inputMeter.Value = Math.Clamp(20 * Math.Log10(Math.Max(.000001, e?.InputPeak ?? 0)) + 60, 0, 60);
        _outputMeter.Value = Math.Clamp(20 * Math.Log10(Math.Max(.000001, e?.OutputPeak ?? 0)) + 60, 0, 60);
        _levels.Text = T("麦克风", "Microphone") + $"  {Db(e?.InputPeak ?? 0)} dB    " + T("输出", "Output") + $"  {Db(e?.OutputPeak ?? 0)} dB";
        _spectrum.Update(e?.SpectrumFrame ?? new float[480]);
        static string Db(float v) => v < .00001f ? "−∞" : (20 * Math.Log10(v)).ToString("F1");
    }
    Window Dialog(string title, Panel panel, double height = 570)
    {
        var window = OneBoxWindow.Create(_window, title, 580, height, new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, true);
        ApplyShell(window); window.Show(); return window;
    }
    void Guide()
    {
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(Text(T("三步开始共享", "Start sharing in three steps"), 20));
        panel.Children.Add(Text(T("1. 从 VB-Audio 官方网站安装 VB-CABLE。安装需要管理员权限，按提示重启电脑。\n\n2. 在 OneBox 选择真实麦克风，输出选择 CABLE Input。播放音乐，选择对应软件，然后开始共享。\n\n3. 在聊天或游戏的语音设置中，将麦克风改为 CABLE Output。耳机输出保持原样。", "1. Install VB-CABLE from VB-Audio. Administrator access and a restart may be needed.\n\n2. Select your microphone and CABLE Input in OneBox. Play music, choose the app, then start sharing.\n\n3. Select CABLE Output as the microphone in your chat or game. Keep your normal headphone output.")));
        panel.Children.Add(Button(T("打开 VB-CABLE 官网", "Open VB-CABLE website"), () => Open("https://vb-audio.com/Cable/")));
        panel.Children.Add(Button(T("降噪运行库 · Microsoft 官方", "Denoising runtime · Microsoft official"), () => Open("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist")));
        panel.Children.Add(Text(T("常见问题", "FAQ"), 17));
        panel.Children.Add(Text(T("• 朋友听不到音乐：确认 BGM 开关和应用选择；适当关闭聊天软件的降噪、自动增益或启用音乐模式。\n• 软件不在列表：先让它播放声音，再等待列表刷新。浏览器可能包含多个标签页的声音。\n• 听到重音：关闭“最终输出”监听；音乐播放器本身已经在播放。\n• 同一 USB 声卡的麦克风与耳机：若监听出现卡顿，可换独立耳机。\n• RNNoise 适合持续底噪；DeepFilterNet3 更适合复杂噪声，但资源消耗和延迟可能更高。\n• 停止或退出 OneBox 后，通话请切回真实麦克风。关闭本窗口会继续运行；退出托盘才会关闭 OneBox。\n• 炸麦为受限幅保护的失真音效，不会绕过输出限幅。", "• No music: check the BGM switch and app selection. Adjust chat noise suppression, automatic gain, or music mode.\n• App missing: play audio and wait for the list to refresh. Browser capture can include multiple tabs.\n• Doubled music: turn off Final output monitoring; your player is already audible.\n• Mic/headphones on the same USB device: try a separate monitor device if playback stutters.\n• RNNoise suits steady noise; DeepFilterNet3 handles complex noise with potentially higher CPU cost and latency.\n• After stopping or exiting OneBox, switch chat back to your physical microphone. Closing this window keeps sharing active.\n• Distortion remains protected by the output limiter.")));
        panel.Children.Add(Button(T("OneBox 项目 / 反馈", "OneBox project / feedback"), () => Open("https://github.com/OneT1er/OneBox")));
        panel.Children.Add(Button("RNNoise", () => Open("https://github.com/xiph/rnnoise")));
        panel.Children.Add(Button("DeepFilterNet3", () => Open("https://github.com/Rikorose/DeepFilterNet")));
        Dialog(T("使用教程", "Guide"), panel, 650);
    }
    void Settings()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        var window = Dialog(T("音频设置", "Audio settings"), panel, 650);
        Combo(panel, T("语言", "Language"), new[] { "中文", "English" }, _controller.Settings.Language == "en" ? "English" : "中文", item =>
        { Change(s => s.Language = (string)item == "English" ? "en" : "zh"); window.Close(); Build(); });
        Combo(panel, T("外观 · OneBox 紫影", "Appearance · OneBox violet"), new[] { T("深色", "Dark"), T("浅色暖调", "Warm light") }, Light ? T("浅色暖调", "Warm light") : T("深色", "Dark"), item =>
        { Change(s => s.Theme = (string)item == T("深色", "Dark") ? "dark" : "light"); window.Close(); Build(); });
        var auto = new CheckBox { Content = T("开机启动 OneBox", "Launch OneBox at sign-in"), Foreground = Foreground, IsChecked = AutoStartService.GetCurrent() != AutoStartMethod.None, Margin = new Thickness(0, 10, 0, 10) };
        auto.Click += async (_, _) =>
        {
            var result = await _owner.ExecuteCommandAsync(AppCommandId.AutoStartApply, CommandSource.Settings,
                new AutoStartApplyPayload(auto.IsChecked == true, AppPrefs.Get(PreferenceKeys.AutoStart.LastMethod)));
            if (!result.Success) auto.IsChecked = AutoStartService.GetCurrent() != AutoStartMethod.None;
        };
        panel.Children.Add(auto);
        AddSetting(T("OneBox 启动后自动共享", "Start sharing when OneBox launches"), s => s.AutoStartAudio, (s, v) => s.AutoStartAudio = v);
        AddSetting(T("未共享时自动检查、下载并安装更新", "Automatically check, download and install updates while idle"), s => s.AutoUpdate, (s, v) => s.AutoUpdate = v);
        panel.Children.Add(Button(T("立即检查 OneBox 更新", "Check OneBox updates now"), () => _ = _owner.ExecuteCommandAsync(AppCommandId.UpdateCheck, CommandSource.Settings, new UpdateCheckPayload(true))));
        panel.Children.Add(Text(T("全局快捷键 · 支持单键或组合键", "Global shortcuts · single keys or combinations"), 15));
        string[] keys = { "Denoise", "Eq", "Music", "Explode", "Monitor" };
        string[] names = { T("降噪", "Denoise"), "EQ", "BGM", T("炸麦", "Distortion"), T("监听", "Monitor") };
        for (int i = 0; i < keys.Length; i++)
        {
            string key = "AudioStudio.Hotkey." + keys[i], name = names[i];
            var row = new WrapPanel();
            int encoded = AppPrefs.GetInt(key, 0);
            var button = Button(name + " · " + (encoded == 0 ? T("无", "None") : HotkeyCaptureDialog.Format(encoded)), () => { });
            button.Click += (_, _) =>
            {
                int old = AppPrefs.GetInt(key, 0);
                int? value = CaptureShortcut(window, old);
                if (!value.HasValue || value.Value == old) return;
                bool duplicate = HotkeyDefinitions.All.Any(x => x.PreferenceKey != key && HotkeyDefinitions.ResolveEncoded(x) == value.Value);
                if (duplicate || !_owner.TestHotkey(value.Value)) { MessageBox.Show(window, T("此快捷键已被占用", "This shortcut is already in use")); return; }
                if (!AppPrefs.SetInt(key, value.Value)) { MessageBox.Show(window, T("保存失败", "Save failed")); return; }
                _owner.RefreshHotkeys(); button.Content = name + " · " + HotkeyCaptureDialog.Format(value.Value);
            };
            row.Children.Add(button);
            row.Children.Add(Button(T("清除", "Clear"), () => { if (AppPrefs.SetInt(key, 0)) { _owner.RefreshHotkeys(); button.Content = name + " · " + T("无", "None"); } }));
            panel.Children.Add(row);
        }
        void AddSetting(string title, Func<StudioSettings, bool> get, Action<StudioSettings, bool> set)
        {
            var box = new CheckBox { Content = title, IsChecked = get(_controller.Settings), Foreground = Foreground, Margin = new Thickness(0, 8, 0, 8) };
            box.Click += (_, _) => Change(s => set(s, box.IsChecked == true)); panel.Children.Add(box);
        }
    }
    int? CaptureShortcut(Window owner, int current)
    {
        int value = current;
        var panel = new StackPanel { Margin = new Thickness(20) };
        var text = Text(T("按下单键或组合键，Esc 取消", "Press a key or combination; Esc cancels")); panel.Children.Add(text);
        var dialog = OneBoxWindow.Create(owner, T("设置快捷键", "Set shortcut"), 360, 170, panel, false); ApplyShell(dialog);
        panel.Children.Add(Button(T("保存", "Save"), () => { dialog.DialogResult = value != 0; }));
        dialog.PreviewKeyDown += (_, e) =>
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { dialog.DialogResult = false; return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            int encodedMods = (mods.HasFlag(ModifierKeys.Alt) ? 1 : 0) | (mods.HasFlag(ModifierKeys.Control) ? 2 : 0) | (mods.HasFlag(ModifierKeys.Shift) ? 4 : 0) | (mods.HasFlag(ModifierKeys.Windows) ? 8 : 0);
            value = (encodedMods << 16) | KeyInterop.VirtualKeyFromKey(key); text.Text = HotkeyCaptureDialog.Format(value); e.Handled = true;
        };
        _owner?.PauseHotkeys();
        try { return dialog.ShowDialog() == true ? value : null; }
        finally { _owner?.RefreshHotkeys(); }
    }
    void Equalizer()
    {
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(Text(T("10 段均衡器", "10-band equalizer"), 19));
        panel.Children.Add(Text(T("拖动圆点改变音色；增益范围 −12 到 +12 dB。只影响人声。", "Drag points to shape your voice, from −12 to +12 dB. Music is unaffected.")));
        var curve = new StudioEqCurve { Gains = (float[])_controller.Settings.Bands.Clone() };
        var names = new[] { "Flat", "Voice", "Warm", "Bright", "Bass", "Radio", "Presence", "Custom" };
        var labels = new[] { T("平直", "Flat"), T("清晰人声", "Voice"), T("温暖", "Warm"), T("明亮", "Bright"), T("低音", "Bass"), T("电台", "Radio"), T("临场感", "Presence"), T("自定义", "Custom") };
        float[][] presets = { new float[10], new float[] { -6,-4,-2,0,1,2,3,2,0,-2 }, new float[] { 0,2,3,2,0,0,-1,-2,-2,-3 }, new float[] { -3,-2,-1,0,0,1,2,3,3,2 }, new float[] { 5,4,3,1,0,0,0,0,0,0 }, new float[] { -12,-10,-6,1,3,4,3,0,-6,-10 }, new float[] { -4,-3,-2,0,1,3,4,3,1,0 } };
        var combo = Combo(panel, T("音色预设", "Tone preset"), labels, labels[Math.Max(0, Array.IndexOf(names, _controller.Settings.EqPreset))], item =>
        {
            int index = Array.IndexOf(labels, (string)item);
            curve.Gains = (float[])(index >= 7 ? _controller.Settings.CustomBands : presets[index]).Clone(); curve.InvalidateVisual();
            Change(s => { s.Bands = (float[])curve.Gains.Clone(); s.EqPreset = names[index]; s.Eq = true; });
        });
        curve.Changed += () => { Change(s => { s.Bands = (float[])curve.Gains.Clone(); s.CustomBands = (float[])curve.Gains.Clone(); s.EqPreset = "Custom"; s.Eq = true; }); combo.SelectedItem = labels[7]; };
        panel.Children.Add(curve);
        panel.Children.Add(Button(T("恢复平直", "Reset to flat"), () => combo.SelectedItem = labels[0]));
        Dialog(T("均衡器", "Equalizer"), panel, 490);
    }
    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
