using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using PowerAudioManager.Commands;

namespace PowerAudioManager.AudioStudio;

// OneMic studio (direction A): a two-column window.  The left column is the
// live "control desk" (start/stop, meters, spectrum, soundpad); the right
// column holds the numbered setup steps.  Per-sound settings, the equalizer
// and the guide open as separate dialogs.
internal sealed class StudioWindow
{
    readonly MainWindow _owner;
    readonly StudioController _controller;
    readonly Window _window;
    readonly Grid _root = new() { Margin = new Thickness(12, 4, 12, 12) };
    // Right-hand column; kept as a field so the scroll position survives Build().
    readonly StackPanel _body = new() { Margin = new Thickness(0, 0, 4, 0) };
    readonly ScrollViewer _rightScroll;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<(CheckBox Box, Func<StudioSettings, bool> Read)> _toggles = new();
    ComboBox _input, _output, _apps, _monitor;
    RadioButton[] _presetButtons;
    TextBlock _status, _levels, _chatNotice;
    TextBlock _soundpadStatus, _cableStatus, _cableHint;
    TextBlock _inDb, _musicDb, _outDb;
    TextBlock _pillText, _effectsSummary;
    System.Windows.Shapes.Ellipse _pillDot;
    Border _pill;
    UniformGrid _pads;
    StackPanel _soundRows;
    Window _soundDialog;
    (string Id, string Message) _soundConflict;
    Button _installCable;
    Button _start;
    StudioSpectrum _spectrum;
    StudioMeter _inputMeter, _musicMeter, _outputMeter;
    bool _building, _refreshing;
    bool _closed;
    bool _effectsExpanded;
    bool? _shownWanted;
    DateTime? _sharingSince;
    int _ticks;
    static Brush B(Color c) => UiKit.FrozenBrush(c);
    Brush Foreground => B(ThemeTokens.PrimaryText);
    Brush Muted => B(ThemeTokens.SecondaryText);
    string T(string zh, string en) => _controller.T(zh, en);
    public event EventHandler Closed;
    public StudioWindow(MainWindow owner, StudioController controller)
    {
        _owner = owner; _controller = controller;
        _rightScroll = new ScrollViewer { Content = _body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _rightScroll.Resources[typeof(ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
        _window = OneBoxWindow.Create(owner, "OneMic", 860, 640, _root, true);
        _window.MinWidth = 760; _window.MinHeight = 560;
        _window.Closed += (_, _) => { _closed = true; _timer.Stop(); _controller.Changed -= Sync; _soundDialog?.Close(); Closed?.Invoke(this, EventArgs.Empty); };
        _window.StateChanged += (_, _) => { if (_window.WindowState == WindowState.Minimized) { _window.Hide(); _window.WindowState = WindowState.Normal; } };
        _window.PreviewKeyDown += SoundpadKeyDown;
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

    // ---- small builders -------------------------------------------------

    TextBlock Text(string text, double size = 12, Color? color = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, Foreground = color.HasValue ? B(color.Value) : Foreground, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        return t;
    }
    Button Btn(string title, Action action, ButtonVariant variant = ButtonVariant.Flat, double height = 30)
    {
        var b = new Button { Content = title, Height = height, Margin = new Thickness(0, 0, 6, 0) };
        UiKit.ApplyButtonVariant(b, variant);
        if (variant == ButtonVariant.Flat) b.Padding = new Thickness(8, 0, 8, 0);
        b.Click += (_, _) => Guard(action);
        return b;
    }
    Button GlyphAction(FrameworkElement glyph, string title, Action action)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        glyph.Margin = new Thickness(0, 0, 6, 0); glyph.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var b = new Button { Content = row, Height = 30, Padding = new Thickness(10, 0, 10, 0) };
        UiKit.ApplyButtonVariant(b, ButtonVariant.Flat);
        AutomationProperties.SetName(b, title);
        b.Click += (_, _) => Guard(action);
        return b;
    }
    void Guard(Action action) { try { action(); } catch (Exception ex) { OneBoxMessage.Error(_window, "OneMic", ex.Message); } }
    void Change(Action<StudioSettings> edit, bool restart = false)
    {
        if (_building) return;
        Guard(() => { var s = _controller.Settings.Copy(); edit(s); _controller.Apply(s); if (restart && _controller.Wanted) _ = _controller.StartAsync(); });
    }
    static Border CardBorder(UIElement child) => new()
    {
        Background = B(ThemeTokens.Card), CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = child
    };
    // Numbered step card: "(1) 连接设备 ........ trailing".
    StackPanel StepCard(Panel parent, string number, string title, UIElement trailing = null)
    {
        var panel = new StackPanel();
        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        if (number != null)
        {
            var badge = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = B(ThemeTokens.Active), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = number, FontSize = 11, Foreground = Foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            DockPanel.SetDock(badge, Dock.Left); header.Children.Add(badge);
        }
        if (trailing != null) { DockPanel.SetDock(trailing, Dock.Right); header.Children.Add(trailing); }
        var heading = Text(title, 12); heading.FontWeight = FontWeights.SemiBold; heading.TextWrapping = TextWrapping.NoWrap;
        header.Children.Add(heading);
        panel.Children.Add(header);
        parent.Children.Add(CardBorder(panel));
        return panel;
    }
    // Labelled combo ("caption above, dark combo below").
    ComboBox Combo(Panel parent, string label, IEnumerable<object> items, object selected, Action<object> changed)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        host.Children.Add(Text(label, 11, ThemeTokens.SecondaryText));
        var combo = new ComboBox { ItemsSource = items, SelectedItem = selected, MinHeight = 32, Margin = new Thickness(0, 4, 0, 0) };
        combo.Style = ThemeTokens.CreateDarkComboBoxStyle(false);
        AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) => { if (!_building && !_refreshing && combo.SelectedItem != null) changed(combo.SelectedItem); };
        host.Children.Add(combo);
        parent.Children.Add(host);
        return combo;
    }
    // Switch registered for Sync(); writes on user click only.
    CheckBox Switch(string text, Func<StudioSettings, bool> read, Action<StudioSettings, bool> write, bool restart = false)
    {
        var box = UiKit.MakeSwitch(text, read(_controller.Settings));
        if (string.IsNullOrEmpty(text)) AutomationProperties.SetName(box, "开关");
        box.Click += (_, _) => Change(s => write(s, box.IsChecked == true), restart);
        _toggles.Add((box, read));
        return box;
    }
    // Switch rendered as a raised tile (used in the voice card).
    Border SwitchTile(string text, Func<StudioSettings, bool> read, Action<StudioSettings, bool> write, bool restart = false)
    {
        var box = Switch(text, read, write, restart);
        box.Margin = new Thickness(0);
        return new Border { Background = B(ThemeTokens.Raised), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 0), Child = box };
    }
    // "label  [■■■■■■|      ]  value" tile slider row.
    Slider SliderRow(Panel parent, string label, double value, double max, Action<float> changed, double min = 0, double labelWidth = 64)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var caption = Text(label, 11, ThemeTokens.SecondaryText); caption.Width = labelWidth; caption.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(caption, Dock.Left); row.Children.Add(caption);
        var readout = Text(Math.Round(value).ToString(), 13); readout.FontWeight = FontWeights.SemiBold; readout.Width = 40; readout.TextAlignment = TextAlignment.Right;
        DockPanel.SetDock(readout, Dock.Right); row.Children.Add(readout);
        var slider = UiKit.MakeTileSlider(min, max, value, 24);
        AutomationProperties.SetName(slider, label);
        slider.ValueChanged += (_, _) => { readout.Text = Math.Round(slider.Value).ToString(); if (!_building) changed((float)slider.Value); };
        row.Children.Add(slider);
        parent.Children.Add(row);
        return slider;
    }
    // "label  [segmented control]" row.
    RadioButton[] SegmentRow(Panel parent, string label, IReadOnlyList<string> options, int selected, Action<int> picked)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var caption = Text(label, 11, ThemeTokens.SecondaryText); caption.Width = 64;
        DockPanel.SetDock(caption, Dock.Left); row.Children.Add(caption);
        var seg = UiKit.MakeSegmented(options, selected, index => { if (!_building) picked(index); }, label);
        row.Children.Add(seg);
        parent.Children.Add(row);
        return ((UniformGrid)seg.Child).Children.OfType<RadioButton>().ToArray();
    }

    // ---- main layout ----------------------------------------------------

    void Build()
    {
        _building = true;
        _root.Children.Clear(); _root.RowDefinitions.Clear(); _root.ColumnDefinitions.Clear();
        _body.Children.Clear(); _toggles.Clear();
        _window.Title = "OneMic";
        var s = _controller.Settings;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header strip: status pill on the left, window actions on the right.
        var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 10) };
        _pillDot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        _pillText = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal }; pillRow.Children.Add(_pillDot); pillRow.Children.Add(_pillText);
        _pill = new Border { CornerRadius = new CornerRadius(11), Height = 22, Padding = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Child = pillRow };
        DockPanel.SetDock(_pill, Dock.Left); header.Children.Add(_pill);
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(GlyphAction(IconCatalog.CreateElement(IconKey.Equalizer, 14, Muted), T("均衡器", "Equalizer"), Equalizer));
        actions.Children.Add(GlyphAction(UiKit.Glyph(UiKit.Glyphs.Help, 14, Muted), T("教程", "Guide"), Guide));
        var settingsButton = UiKit.MakeIconButton(IconKey.Settings, T("OneBox 设置", "OneBox settings"));
        settingsButton.Click += (_, _) => Guard(OpenSettings);
        actions.Children.Add(settingsButton);
        var hide = new Button { Width = 32, Height = 32, Content = UiKit.Glyph(UiKit.Glyphs.Minus, 16, Muted), ToolTip = T("收起到托盘", "Hide to tray") };
        UiKit.ApplyIconButtonStyle(hide); AutomationProperties.SetName(hide, T("收起到托盘", "Hide to tray"));
        hide.Click += (_, _) => _window.Hide();
        actions.Children.Add(hide);
        DockPanel.SetDock(actions, Dock.Right); header.Children.Add(actions);
        Grid.SetRow(header, 0); _root.Children.Add(header);

        var columns = new Grid();
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(columns, 1); _root.Children.Add(columns);

        var left = BuildControlDesk(s);
        Grid.SetColumn(left, 0); columns.Children.Add(left);
        if (_rightScroll.Parent is Panel oldParent) oldParent.Children.Remove(_rightScroll);
        Grid.SetColumn(_rightScroll, 2); columns.Children.Add(_rightScroll);

        BuildDevicesStep();
        BuildVoiceStep(s);
        BuildMusicStep(s);
        BuildEffectsCard(s);

        _shownWanted = null;
        _building = false; Sync(); _ = RefreshDevices();
    }

    void OpenSettings()
    {
        _window.Hide();
        try { SettingsDialog.Show(_owner, SettingsDialog.OneMicTabIndex); }
        finally
        {
            if (!_closed && MainWindow.ModuleVisible("OneMic"))
            {
                Build(); _window.Show(); _window.Activate();
            }
        }
    }

    Grid BuildControlDesk(StudioSettings s)
    {
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Hero card: start/stop, hint, meters, spectrum, capture status.
        var hero = new StackPanel();
        _start = new Button { Height = 44, FontSize = 14, Margin = new Thickness(0, 0, 0, 10), HorizontalContentAlignment = HorizontalAlignment.Center };
        _start.Click += (_, _) => Guard(() => { if (_controller.Wanted) _ = _controller.StopAsync(); else _ = _controller.StartAsync(); });
        hero.Children.Add(_start);
        var hint = new TextBlock { FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
        hint.Inlines.Add(new Run(T("通话或游戏里把麦克风选为 ", "In your chat or game, choose ")));
        hint.Inlines.Add(new Run("CABLE Output") { FontFamily = UiKit.MonoFont, Foreground = Foreground });
        hero.Children.Add(hint);
        _status = Text("", 11, ThemeTokens.TertiaryText); _status.Margin = new Thickness(0, 0, 0, 8);
        hero.Children.Add(_status);
        _inputMeter = new StudioMeter { Height = 6, ToolTip = T("麦克风电平", "Microphone level") };
        _musicMeter = new StudioMeter { Height = 6, ToolTip = "BGM", Fill = UiKit.FrozenBrush(Color.FromRgb(111, 183, 232)) };
        _outputMeter = new StudioMeter { Height = 6, ToolTip = T("输出电平", "Output level"), Fill = UiKit.FrozenBrush(ThemeTokens.Success) };
        hero.Children.Add(MeterRow(T("麦克风", "Mic"), _inputMeter, out _inDb));
        hero.Children.Add(MeterRow("BGM", _musicMeter, out _musicDb));
        hero.Children.Add(MeterRow(T("输出", "Output"), _outputMeter, out _outDb));
        _spectrum = new StudioSpectrum { Height = 36 };
        hero.Children.Add(new Border { Background = B(ThemeTokens.Background), CornerRadius = new CornerRadius(8), Padding = new Thickness(6), Margin = new Thickness(0, 4, 0, 8), Child = _spectrum });
        _levels = Text("", 11, ThemeTokens.SecondaryText);
        hero.Children.Add(_levels);
        _chatNotice = Text("", 11, ThemeTokens.SecondaryText); _chatNotice.Margin = new Thickness(0, 4, 0, 0);
        hero.Children.Add(_chatNotice);
        var heroCard = CardBorder(hero);
        Grid.SetRow(heroCard, 0); left.Children.Add(heroCard);

        // Soundpad card.
        var pad = new DockPanel { LastChildFill = true };
        var padHeader = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var stop = Btn(T("停止 · Ctrl+Shift+0", "Stop · Ctrl+Shift+0"), () => { _controller.StopSound(); _soundpadStatus.Text = "已停止"; }, ButtonVariant.Flat, 26);
        stop.FontSize = 11; stop.Margin = new Thickness(0);
        DockPanel.SetDock(stop, Dock.Right); padHeader.Children.Add(stop);
        var manage = Btn(T("设置", "Settings"), SoundSettings, ButtonVariant.Flat, 26);
        manage.FontSize = 11; manage.ToolTip = "每条音效的音量和快捷键";
        DockPanel.SetDock(manage, Dock.Right); padHeader.Children.Add(manage);
        var padTitle = Text(T("音效板", "Soundpad"), 12); padTitle.FontWeight = FontWeights.SemiBold;
        padHeader.Children.Add(padTitle);
        DockPanel.SetDock(padHeader, Dock.Top); pad.Children.Add(padHeader);

        var volume = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        SliderRow(volume, T("总音量", "Volume"), s.SoundpadGain * 100, 200, value => Change(x => x.SoundpadGain = value / 100), 0, 48);
        _soundpadStatus = Text("", 11, ThemeTokens.TertiaryText);
        volume.Children.Add(_soundpadStatus);
        DockPanel.SetDock(volume, Dock.Bottom); pad.Children.Add(volume);

        _pads = new UniformGrid { Columns = 3 };
        var padScroll = new ScrollViewer { Content = _pads, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        padScroll.Resources[typeof(ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
        pad.Children.Add(padScroll);
        var padCard = CardBorder(pad); padCard.Margin = new Thickness(0);
        Grid.SetRow(padCard, 1); left.Children.Add(padCard);
        RefreshSoundpadRows();
        return left;
    }

    DockPanel MeterRow(string label, StudioMeter meter, out TextBlock db)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
        var caption = Text(label, 11, ThemeTokens.SecondaryText); caption.Width = 44; caption.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(caption, Dock.Left); row.Children.Add(caption);
        db = Text("−∞ dB", 11); db.Width = 56; db.TextAlignment = TextAlignment.Right; db.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(db, Dock.Right); row.Children.Add(db);
        meter.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(meter);
        return row;
    }

    void BuildDevicesStep()
    {
        _cableStatus = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Text = "正在检查 VB-CABLE…", Foreground = Muted };
        var devices = StepCard(_body, "1", T("连接设备", "Connect devices"), _cableStatus);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new StackPanel(); var right = new StackPanel();
        _input = Combo(left, T("你的麦克风", "Your microphone"), Array.Empty<object>(), null, item => Change(x => x.InputId = ((StudioDevice)item).Id, true));
        _output = Combo(right, T("分享给朋友 · 虚拟输出", "Share with friends · virtual output"), Array.Empty<object>(), null, item => Change(x => x.OutputId = ((StudioDevice)item).Id, true));
        Grid.SetColumn(left, 0); Grid.SetColumn(right, 2);
        grid.Children.Add(left); grid.Children.Add(right);
        devices.Children.Add(grid);
        _cableHint = Text("", 11, ThemeTokens.SecondaryText);
        devices.Children.Add(_cableHint);
        _installCable = Btn("安装 VB-CABLE 官方驱动", () => _ = VbCableInstaller.InstallAsync(_window), ButtonVariant.Primary, 32);
        _installCable.HorizontalAlignment = HorizontalAlignment.Left; _installCable.Margin = new Thickness(0, 8, 0, 0);
        devices.Children.Add(_installCable);
    }

    void BuildVoiceStep(StudioSettings s)
    {
        var benchmarkOutput = Text("", 11, ThemeTokens.SecondaryText);
        var bench = Btn(T("测试各模式延迟", "Test mode latency"), () => _ = BenchmarkAsync(benchmarkOutput), ButtonVariant.Flat, 26);
        bench.FontSize = 11; bench.Margin = new Thickness(0);
        var voice = StepCard(_body, "2", T("调整人声", "Adjust your voice"), bench);

        var presets = new[] { T("安静", "Quiet"), T("标准", "Standard"), T("嘈杂", "Noisy"), T("直播", "Live"), T("自定义", "Custom") };
        var keys = new[] { "Quiet", "Standard", "Noisy", "Live", "Custom" };
        _presetButtons = SegmentRow(voice, T("场景", "Preset"), presets, Math.Max(0, Array.IndexOf(keys, s.Preset)), index =>
        {
            Change(x => { string key = keys[index]; if (key == "Custom") x.Preset = key; else x.ApplyPreset(key); }, true);
            // Recreate model and slider controls after the selection event has
            // completed so the sliders show the preset's values.
            _window.Dispatcher.BeginInvoke((Action)Build);
        });

        var models = StudioDenoisers.Factories.Keys.ToArray();
        var modelLabels = models.Select(m => m == nameof(DenoiseMode.Off) ? T("关闭", "Off") : m).ToArray();
        SegmentRow(voice, T("降噪", "Denoise"), modelLabels, Math.Max(0, Array.IndexOf(models, s.Model)), index =>
            Change(x => { x.Model = models[index]; x.Denoise = x.Model != nameof(DenoiseMode.Off); x.Preset = "Custom"; }, true));
        voice.Children.Add(benchmarkOutput);

        SliderRow(voice, T("降噪强度", "Reduction"), s.Strength * 100, 100, value => Change(x => { x.Strength = value / 100; x.Preset = "Custom"; }));
        SliderRow(voice, T("人声音量", "Voice"), s.MicGain * 100, 300, value => Change(x => { x.MicGain = value / 100; x.Preset = "Custom"; }));

        var tiles = new UniformGrid { Columns = 3, Margin = new Thickness(0, 2, -6, 0) };
        tiles.Children.Add(SwitchTile(T("混入麦克风", "Include mic"), x => x.Microphone, (x, v) => x.Microphone = v, true));
        tiles.Children.Add(SwitchTile(T("实时降噪", "Denoise"), x => x.Denoise, (x, v) => x.Denoise = v, true));
        tiles.Children.Add(SwitchTile(T("10 段均衡器", "10-band EQ"), x => x.Eq, (x, v) => x.Eq = v));
        voice.Children.Add(tiles);
    }

    void BuildMusicStep(StudioSettings s)
    {
        var musicSwitch = Switch(null, x => x.Music, (x, v) => x.Music = v, true);
        AutomationProperties.SetName(musicSwitch, T("分享应用音乐", "Share application music"));
        var music = StepCard(_body, "3", T("分享音乐", "Share music"), musicSwitch);
        var note = Text(T("只分享所选应用。你仍通过播放器正常听音乐。", "Only the selected app is shared. Keep listening through your player as usual."), 11, ThemeTokens.SecondaryText);
        note.Margin = new Thickness(0, -4, 0, 8);
        music.Children.Add(note);
        _apps = Combo(music, T("先播放音乐，再选择应用", "Play music, then select the app"), Array.Empty<object>(), null, item => Change(x =>
        { x.ApplicationPath = ((StudioApplication)item).Path; x.ApplicationPid = ((StudioApplication)item).Pid; }, true));
        SliderRow(music, T("朋友听到", "Friends hear"), s.MusicGain * 100, 200, value => Change(x => x.MusicGain = value / 100));
    }

    void BuildEffectsCard(StudioSettings s)
    {
        var panel = new StackPanel();
        var toggle = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, Height = 40, Padding = new Thickness(0) };
        UiKit.ApplyButtonVariant(toggle, ButtonVariant.Flat);
        toggle.Padding = new Thickness(0); toggle.Background = Brushes.Transparent;
        var head = new DockPanel { LastChildFill = true };
        var chevron = IconCatalog.CreateElement(_effectsExpanded ? IconKey.ChevronUp : IconKey.ChevronDown, 12, Muted);
        DockPanel.SetDock(chevron, Dock.Right); head.Children.Add(chevron);
        _effectsSummary = Text("", 11, ThemeTokens.SecondaryText); _effectsSummary.Margin = new Thickness(0, 0, 8, 0); _effectsSummary.TextWrapping = TextWrapping.NoWrap;
        DockPanel.SetDock(_effectsSummary, Dock.Right); head.Children.Add(_effectsSummary);
        var title = Text(T("监听与炸麦", "Monitoring and distortion"), 12); title.FontWeight = FontWeights.SemiBold; title.TextWrapping = TextWrapping.NoWrap;
        head.Children.Add(title);
        toggle.Content = head;
        AutomationProperties.SetName(toggle, T("监听与炸麦", "Monitoring and distortion"));
        panel.Children.Add(toggle);

        var content = new StackPanel { Visibility = _effectsExpanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        var note = Text(T("本地试听默认只播放人声；选最终输出会把播放器音乐再播一遍。", "Local monitoring defaults to voice only; Final output also replays music from your player."), 11, ThemeTokens.SecondaryText);
        note.Margin = new Thickness(0, 0, 0, 8);
        content.Children.Add(note);
        _monitor = Combo(content, "本地音效 / 监听耳机（默认使用系统输出）", Array.Empty<object>(), null, item => Change(x => x.MonitorId = ((StudioDevice)item).Id, true));
        string[] points = { T("原始输入", "Raw input"), T("降噪后", "After denoise"), T("增益后", "After gain"), T("EQ 后", "After EQ"), T("最终输出", "Final output") };
        Combo(content, T("监听位置", "Monitor point"), points, points[Math.Clamp(s.MonitorPoint, 0, points.Length - 1)], item => Change(x => x.MonitorPoint = Array.IndexOf(points, (string)item)));
        var monitorSwitch = Switch(T("开启监听", "Enable monitoring"), x => x.Monitor, (x, v) => x.Monitor = v, true);
        monitorSwitch.Margin = new Thickness(0, 0, 0, 8);
        content.Children.Add(monitorSwitch);
        SliderRow(content, T("耳机试听", "Monitor"), s.MonitorGain * 100, 100, value => Change(x => x.MonitorGain = value / 100));
        var explodeSwitch = Switch(T("炸麦音效（受输出限幅保护）", "Distortion effect (limited)"), x => x.Explode, (x, v) => x.Explode = v);
        explodeSwitch.Margin = new Thickness(0, 0, 0, 8);
        content.Children.Add(explodeSwitch);
        SliderRow(content, T("炸麦强度", "Intensity"), s.ExplodeStrength * 100, 100, value => Change(x => x.ExplodeStrength = value / 100), 1);
        panel.Children.Add(content);

        toggle.Click += (_, _) =>
        {
            _effectsExpanded = content.Visibility != Visibility.Visible;
            content.Visibility = _effectsExpanded ? Visibility.Visible : Visibility.Collapsed;
            head.Children.Remove(chevron);
            chevron = IconCatalog.CreateElement(_effectsExpanded ? IconKey.ChevronUp : IconKey.ChevronDown, 12, Muted);
            DockPanel.SetDock(chevron, Dock.Right);
            head.Children.Insert(0, chevron);
        };
        var card = CardBorder(panel); card.Padding = new Thickness(14, 2, 14, 8);
        _body.Children.Add(card);
    }

    // ---- soundpad ---------------------------------------------------------

    void AddSounds()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择音效文件",
            Filter = "音频文件|*.wav;*.mp3;*.wma;*.aif;*.aiff;*.m4a|所有文件|*.*",
            Multiselect = true
        };
        if (picker.ShowDialog(_soundDialog ?? _window) != true) return;
        var settings = _controller.Settings.Copy();
        int previousCount = settings.SoundEffects.Count;
        foreach (string path in picker.FileNames)
        {
            if (settings.SoundEffects.Count >= StudioSoundpadHotkeys.MaxEffects) break;
            if (!settings.SoundEffects.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase)))
                settings.SoundEffects.Add(new StudioSoundEffect { Path = path });
        }
        _controller.Apply(settings);
        if (previousCount == 0 && settings.SoundEffects.Count > 0 && _controller.Wanted)
            _ = _controller.StartAsync();
        _owner.RefreshHotkeys();
        RefreshSoundpadRows();
    }

    static string SoundName(StudioSoundEffect effect) => Path.GetFileNameWithoutExtension(effect.Path);

    void RemoveSound(string id)
    {
        if (_controller.Engine?.PlayingSoundId == id) _controller.StopSound();
        var changed = _controller.Settings.Copy();
        changed.SoundEffects.RemoveAll(x => x.Id == id);
        _controller.Apply(changed);
        _owner.RefreshHotkeys();
        RefreshSoundpadRows();
    }

    // Rebuilds the soundpad pads (main window) and, when open, the rows of the
    // per-sound settings dialog.
    void RefreshSoundpadRows()
    {
        RefreshPads();
        RefreshSoundSettingsRows();
    }

    void RefreshPads()
    {
        if (_pads == null) return;
        _pads.Children.Clear();
        foreach (var effect in _controller.Settings.SoundEffects)
        {
            string id = effect.Id;
            bool missing = !File.Exists(effect.Path);
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            content.Children.Add(new TextBlock { Text = SoundName(effect), FontSize = 12, Foreground = missing ? B(ThemeTokens.DisabledText) : Foreground, TextTrimming = TextTrimming.CharacterEllipsis });
            content.Children.Add(new TextBlock
            {
                Text = missing ? "文件丢失" : effect.Hotkey == 0 ? "未设置" : HotkeyCaptureDialog.Format(effect.Hotkey),
                FontSize = 10, FontFamily = UiKit.MonoFont, Foreground = B(missing ? ThemeTokens.Warn : ThemeTokens.TertiaryText),
                Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis
            });
            var b = new Button { Content = content, Height = 52, Margin = new Thickness(0, 0, 6, 6), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 0, 8, 0), ToolTip = effect.Path };
            UiKit.ApplyButtonVariant(b, ButtonVariant.Flat);
            b.Background = B(ThemeTokens.Raised);
            AutomationProperties.SetName(b, "播放 " + SoundName(effect));
            b.Click += (_, _) => _ = PlaySoundAsync(id);
            _pads.Children.Add(b);
        }
        var addContent = new StackPanel { Orientation = Orientation.Horizontal };
        addContent.Children.Add(UiKit.Glyph(UiKit.Glyphs.Plus, 12, Muted, 2.4));
        addContent.Children.Add(new TextBlock { Text = "添加", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var add = new Button { Content = addContent, Height = 52, Margin = new Thickness(0, 0, 6, 6), ToolTip = "支持 WAV、MP3 和 Windows 可解码的音频文件" };
        UiKit.ApplyButtonVariant(add, ButtonVariant.Flat);
        add.BorderBrush = B(Color.FromRgb(74, 70, 96));
        add.Click += (_, _) => Guard(AddSounds);
        AutomationProperties.SetName(add, "添加音效");
        _pads.Children.Add(add);
    }

    void SoundSettings()
    {
        if (_soundDialog != null) { _soundDialog.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
        var top = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        top.Children.Add(Btn("添加音效", AddSounds, ButtonVariant.Primary, 32));
        var stop = Btn("停止音效 · Ctrl+Shift+0", () => { _controller.StopSound(); if (_soundpadStatus != null) _soundpadStatus.Text = "已停止"; }, ButtonVariant.Secondary, 32);
        top.Children.Add(stop);
        panel.Children.Add(top);
        panel.Children.Add(new TextBlock { Text = "支持 WAV、MP3 等 Windows 可解码的音频，单个最长 2 分钟。", FontSize = 11, Foreground = Muted, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        var total = new StackPanel();
        SliderRow(total, "总音效音量", _controller.Settings.SoundpadGain * 100, 200, value => Change(x => x.SoundpadGain = value / 100), 0, 72);
        panel.Children.Add(CardBorder(total));
        panel.Children.Add(new TextBlock { Text = "仅调整这个音效的音量", FontSize = 11, Foreground = B(ThemeTokens.TertiaryText), Margin = new Thickness(4, 4, 0, 6) });
        _soundRows = new StackPanel();
        panel.Children.Add(_soundRows);
        var close = Btn("关闭", () => _soundDialog?.Close(), ButtonVariant.Secondary, 32);
        close.HorizontalAlignment = HorizontalAlignment.Right; close.Margin = new Thickness(0, 8, 0, 0); close.Width = 80;
        panel.Children.Add(close);
        _soundConflict = default;
        _soundDialog = Dialog("音效设置", panel, 520, 560);
        _soundDialog.Closed += (_, _) => { _soundDialog = null; _soundRows = null; _soundConflict = default; };
        RefreshSoundSettingsRows();
    }

    void RefreshSoundSettingsRows()
    {
        if (_soundRows == null) return;
        _soundRows.Children.Clear();
        var effects = _controller.Settings.SoundEffects;
        if (effects.Count == 0)
        {
            _soundRows.Children.Add(Text("还没有音效。支持 WAV、MP3 和 Windows 可解码的音频文件。", 12, ThemeTokens.SecondaryText));
            return;
        }
        foreach (var effect in effects)
        {
            string id = effect.Id;
            bool missing = !File.Exists(effect.Path);
            var row = new DockPanel { LastChildFill = true };

            var play = new Button { Width = 32, Height = 32, Content = UiKit.Glyph(UiKit.Glyphs.Play, 14, Foreground, 2, Foreground), ToolTip = "试听" };
            UiKit.ApplyIconButtonStyle(play); AutomationProperties.SetName(play, "播放 " + SoundName(effect));
            play.Click += (_, _) => _ = PlaySoundAsync(id);
            DockPanel.SetDock(play, Dock.Left); row.Children.Add(play);

            var delete = UiKit.MakeIconButton(IconKey.Delete, "移除 " + SoundName(effect), 28, 14);
            delete.Click += (_, _) => Guard(() => RemoveSound(id));
            DockPanel.SetDock(delete, Dock.Right); row.Children.Add(delete);

            // Hotkey chip + set/clear.
            var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) };
            bool conflict = _soundConflict.Id == id;
            if (effect.Hotkey != 0)
            {
                keys.Children.Add(UiKit.KeyChip(HotkeyCaptureDialog.Format(effect.Hotkey)));
                var clear = Btn("清除", () => ClearSoundShortcut(id), ButtonVariant.Flat, 26); clear.FontSize = 11; clear.Margin = new Thickness(2, 0, 0, 0);
                keys.Children.Add(clear);
            }
            else keys.Children.Add(Text("未设置", 11, ThemeTokens.DisabledText));
            var set = Btn(effect.Hotkey == 0 ? "设置" : "修改", () => TrySetSoundShortcut(id), ButtonVariant.Flat, 26);
            set.FontSize = 11; set.Margin = new Thickness(2, 0, 0, 0);
            if (effect.Hotkey == 0) set.Foreground = B(ThemeTokens.Accent);
            set.ToolTip = "设置此音效的全局快捷键；支持 F1–F24 单键或组合键";
            keys.Children.Add(set);
            DockPanel.SetDock(keys, Dock.Right); row.Children.Add(keys);

            // Per-sound gain.
            float pendingGain = effect.Gain;
            var gainValue = Text(Math.Round(pendingGain * 100).ToString(), 12); gainValue.Width = 32; gainValue.TextAlignment = TextAlignment.Right; gainValue.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(gainValue, Dock.Right); row.Children.Add(gainValue);
            var gain = UiKit.MakeTileSlider(0, 200, pendingGain * 100, 22);
            gain.Width = 110; gain.Margin = new Thickness(8, 0, 4, 0); gain.ToolTip = "仅调整这个音效的音量";
            AutomationProperties.SetName(gain, SoundName(effect) + " 音量");
            var saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            void SaveGain()
            {
                saveTimer.Stop();
                var changed = _controller.Settings.Copy();
                var target = changed.SoundEffects.FirstOrDefault(x => x.Id == id);
                if (target == null || Math.Abs(target.Gain - pendingGain) < .001f) return;
                target.Gain = pendingGain;
                _controller.Apply(changed);
            }
            saveTimer.Tick += (_, _) => SaveGain();
            gain.ValueChanged += (_, _) =>
            {
                pendingGain = (float)(gain.Value / 100);
                gainValue.Text = Math.Round(gain.Value).ToString();
                _controller.Engine?.SetSoundGain(id, pendingGain);
                saveTimer.Stop(); saveTimer.Start();
            };
            gain.PreviewMouseLeftButtonUp += (_, _) => SaveGain();
            gain.PreviewKeyUp += (_, _) => SaveGain();
            row.Unloaded += (_, _) => SaveGain();
            DockPanel.SetDock(gain, Dock.Right); row.Children.Add(gain);

            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
            names.Children.Add(new TextBlock { Text = SoundName(effect), FontSize = 12, Foreground = Foreground, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = effect.Path });
            string ext = Path.GetExtension(effect.Path).TrimStart('.').ToUpperInvariant();
            names.Children.Add(new TextBlock { Text = missing ? ext + " · 文件丢失" : ext, FontSize = 11, Foreground = B(missing ? ThemeTokens.Warn : ThemeTokens.TertiaryText) });
            row.Children.Add(names);

            var holder = new StackPanel();
            holder.Children.Add(row);
            if (conflict)
                holder.Children.Add(new TextBlock { Text = _soundConflict.Message, FontSize = 11, Foreground = B(ThemeTokens.Warn), Margin = new Thickness(38, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            _soundRows.Children.Add(new Border
            {
                Background = B(conflict ? ThemeTokens.WarnSurface : ThemeTokens.Card),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(0, 0, 0, 4), Child = holder
            });
        }
    }

    void TrySetSoundShortcut(string id)
    {
        try { _soundConflict = default; SetSoundShortcut(id); }
        catch (InvalidOperationException ex)
        {
            _soundConflict = (id, ex.Message);
            RefreshSoundpadRows();
            if (_soundDialog == null) OneBoxMessage.Error(_window, "OneMic", ex.Message);
        }
    }
    void SetSoundShortcut(string id)
    {
        var effect = _controller.Settings.SoundEffects.FirstOrDefault(x => x.Id == id);
        if (effect == null) return;
        _owner.PauseHotkeys();
        try
        {
            int? captured = HotkeyCaptureDialog.Show(_soundDialog ?? _window, effect.Hotkey, true);
            if (!captured.HasValue || captured.Value == effect.Hotkey) return;
            int encoded = captured.Value;
            if (!StudioSoundpadHotkeys.IsValid(encoded) || encoded == StudioSoundpadHotkeys.StopEncoded ||
                _controller.Settings.SoundEffects.Any(x => x.Id != id && x.Hotkey == encoded) ||
                HotkeyDefinitions.All.Any(x => HotkeyDefinitions.ResolveEncoded(x) == encoded) ||
                DevicePrefs.GetAllHotkeys().Any(x => x.Value == encoded))
                throw new InvalidOperationException("快捷键与现有功能冲突，请换一个组合。");
            if (!_owner.TestHotkey(encoded))
                throw new InvalidOperationException("快捷键已被其他软件占用，请换一个组合。");
            var changed = _controller.Settings.Copy();
            changed.SoundEffects.First(x => x.Id == id).Hotkey = encoded;
            _controller.Apply(changed);
        }
        finally { _owner.RefreshHotkeys(); RefreshSoundpadRows(); }
    }
    void ClearSoundShortcut(string id)
    {
        var changed = _controller.Settings.Copy();
        var effect = changed.SoundEffects.FirstOrDefault(x => x.Id == id);
        if (effect == null) return;
        effect.Hotkey = 0;
        _controller.Apply(changed);
        if (_soundConflict.Id == id) _soundConflict = default;
        _owner.RefreshHotkeys();
        RefreshSoundpadRows();
    }
    async Task PlaySoundAsync(string id)
    {
        try
        {
            var effect = _controller.Settings.SoundEffects.FirstOrDefault(x => x.Id == id);
            if (effect == null) return;
            _soundpadStatus.Text = "正在加载音效…";
            if (await _controller.PlaySoundAsync(id))
                _soundpadStatus.Text = "正在播放：" + Path.GetFileNameWithoutExtension(effect.Path);
        }
        catch (Exception ex)
        {
            _soundpadStatus.Text = "播放失败：" + ex.Message;
            AppLog.Log("OneMic soundpad", ex);
        }
    }
    void SoundpadKeyDown(object sender, KeyEventArgs e)
    {
        int modifiers = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= 1;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= 2;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= 8;
        int key = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
        int encoded = (modifiers << 16) | (key & 0xFFFF);
        if (encoded == StudioSoundpadHotkeys.StopEncoded)
        {
            e.Handled = true;
            _controller.StopSound();
            if (_soundpadStatus != null) _soundpadStatus.Text = "已停止";
            return;
        }
        var effect = _controller.Settings.SoundEffects.FirstOrDefault(x => x.Hotkey != 0 && x.Hotkey == encoded);
        if (effect == null) return;
        e.Handled = true;
        _ = PlaySoundAsync(effect.Id);
    }
    async Task BenchmarkAsync(TextBlock output)
    {
        if (_controller.Wanted)
        {
            output.Text = T("请先停止共享，以免测试影响通话声音。", "Stop sharing before benchmarking to avoid affecting your call.");
            return;
        }
        output.Text = T("正在现场测量…", "Measuring on this device…");
        var lines = new List<string>();
        foreach (string mode in StudioDenoisers.Factories.Keys)
        {
            try
            {
                var value = await Task.Run(() => StudioBenchmark.Run(mode));
                lines.Add($"{mode}: {value.MetricLabel} {value.Inference:F2} ms · Pipeline {value.Pipeline:F2} ms · " +
                    $"P50 {value.P50:F2} · P95 {value.P95:F2} · Max {value.Maximum:F2} ms · RTF {value.Rtf:F2}");
            }
            catch (Exception ex) { lines.Add(mode + ": " + ex.Message); }
            output.Text = string.Join("\n", lines);
        }
        output.Margin = new Thickness(64, 0, 0, 8);
    }
    async Task RefreshDevices()
    {
        if (_refreshing) return; _refreshing = true;
        try
        {
            var devices = await Task.Run(() => (StudioDevices.List(DataFlow.Capture), StudioDevices.List(DataFlow.Render),
                StudioDevices.Applications(), StudioDevices.CaptureClients()));
            if (_window.Dispatcher.HasShutdownStarted) return;
            var s = _controller.Settings;
            var cables = devices.Item2.Where(x => x.IsVbCableInput).ToArray();
            if (cables.Length == 1 && !cables.Any(x => x.Id == s.OutputId))
            {
                var updated = s.Copy(); updated.OutputId = cables[0].Id;
                _controller.Apply(updated);
                s = _controller.Settings;
                if (_controller.Wanted) _ = _controller.StartAsync();
            }
            Set(_input, devices.Item1, s.InputId); Set(_output, cables, s.OutputId);
            Set(_monitor, devices.Item2.Where(x => !x.IsCable).ToArray(), s.MonitorId);
            bool hasCable = cables.Length > 0;
            _cableStatus.Text = hasCable ? "✓ VB-CABLE 已安装" : "未检测到 VB-CABLE";
            _cableStatus.Foreground = B(hasCable ? Color.FromRgb(143, 224, 176) : ThemeTokens.Warn);
            _cableHint.Text = hasCable
                ? "已检测到 VB-CABLE。请在通话软件中把麦克风选为 CABLE Output。"
                : "未检测到 VB-CABLE。点击下方按钮下载并打开官方安装程序。";
            _installCable.Visibility = hasCable ? Visibility.Collapsed : Visibility.Visible;
            var chat = devices.Item4.FirstOrDefault(x => x.Active && !x.IsVbCable)
                ?? devices.Item4.FirstOrDefault(x => x.Active && x.IsVbCable);
            _chatNotice.Text = chat == null ? "" : chat.IsVbCable
                ? T($"{chat.Application} 正在使用 CABLE Output 作为麦克风。", $"{chat.Application} is using CABLE Output as its microphone.")
                : T($"{chat.Application} 正在使用“{chat.Device}”；请在通话软件中改选 CABLE Output。",
                    $"{chat.Application} is using {chat.Device}; select CABLE Output in the chat app.");
            _chatNotice.Foreground = B(chat != null && !chat.IsVbCable ? ThemeTokens.Warn : ThemeTokens.SecondaryText);
            if (!_apps.IsDropDownOpen)
            {
                _apps.ItemsSource = devices.Item3;
                _apps.SelectedItem = devices.Item3.FirstOrDefault(x => x.Pid == s.ApplicationPid && string.Equals(x.Path, s.ApplicationPath, StringComparison.OrdinalIgnoreCase))
                    ?? devices.Item3.FirstOrDefault(x => string.Equals(x.Path, s.ApplicationPath, StringComparison.OrdinalIgnoreCase));
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
    void SyncStartButton(bool wanted)
    {
        if (_shownWanted == wanted) return;
        _shownWanted = wanted;
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        if (wanted)
        {
            UiKit.ApplyButtonVariant(_start, ButtonVariant.Danger);
            _start.Background = B(ThemeTokens.DangerSurface);
            _start.Foreground = B(Color.FromRgb(242, 197, 205));
            _start.BorderBrush = B(Color.FromRgb(90, 58, 68));
            label.Children.Add(UiKit.Glyph("M6,6 L18,6 L18,18 L6,18 Z", 12, _start.Foreground, 2, _start.Foreground));
            label.Children.Add(new TextBlock { Text = T("停止共享", "Stop sharing"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        else
        {
            UiKit.ApplyButtonVariant(_start, ButtonVariant.Primary);
            _start.ClearValue(Control.BackgroundProperty);
            _start.ClearValue(Control.ForegroundProperty);
            _start.ClearValue(Control.BorderBrushProperty);
            var fg = B(ThemeTokens.OnAccent);
            label.Children.Add(UiKit.Glyph(UiKit.Glyphs.Play, 12, fg, 2, fg));
            label.Children.Add(new TextBlock { Text = T("开始共享", "Start sharing"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        _start.FontWeight = FontWeights.SemiBold;
        _start.FontSize = 14;
        _start.Content = label;
        AutomationProperties.SetName(_start, wanted ? T("停止共享", "Stop sharing") : T("开始共享", "Start sharing"));
    }
    void SyncPill(bool wanted, bool busy)
    {
        if (wanted && _sharingSince == null) _sharingSince = DateTime.UtcNow;
        if (!wanted) _sharingSince = null;
        string text; Color bg, fg, dot;
        if (busy) { text = T("正在切换…", "Switching…"); bg = ThemeTokens.Raised; fg = ThemeTokens.SecondaryText; dot = ThemeTokens.Warn; }
        else if (wanted)
        {
            var elapsed = DateTime.UtcNow - _sharingSince.Value;
            text = T("共享中 · ", "Sharing · ") + elapsed.ToString(@"hh\:mm\:ss");
            bg = ThemeTokens.SuccessSurface; fg = Color.FromRgb(143, 224, 176); dot = ThemeTokens.Success;
        }
        else { text = T("未共享", "Not sharing"); bg = ThemeTokens.Raised; fg = ThemeTokens.SecondaryText; dot = ThemeTokens.DisabledText; }
        if (_pillText.Text != text) _pillText.Text = text;
        _pill.Background = B(bg); _pillText.Foreground = B(fg); _pillDot.Fill = B(dot);
    }
    void Sync()
    {
        if (_status == null) return;
        string status = _controller.Status;
        int separator = status.IndexOf(" / ", StringComparison.Ordinal);
        string shortStatus = separator < 0 ? status : status.Substring(0, separator);
        if (_status.Text != shortStatus) _status.Text = shortStatus;
        bool wanted = _controller.Wanted;
        SyncStartButton(wanted);
        SyncPill(wanted, _controller.Busy);
        _start.IsEnabled = true;
        bool building = _building; _building = true;
        try
        {
            foreach (var (box, read) in _toggles) { bool v = read(_controller.Settings); if (box.IsChecked != v) box.IsChecked = v; }
            if (_presetButtons != null && _controller.Settings.Preset == "Custom" && _presetButtons.Length == 5 && _presetButtons[4].IsChecked != true)
                _presetButtons[4].IsChecked = true;
        }
        finally { _building = building; }
        if (_effectsSummary != null)
        {
            var st = _controller.Settings;
            string summary = (st.Monitor ? T("监听开", "Monitor on") : T("监听关", "Monitor off")) + " · " + (st.Explode ? T("炸麦开", "Distortion on") : T("炸麦关", "Distortion off"));
            if (_effectsSummary.Text != summary) _effectsSummary.Text = summary;
        }
        var e = _controller.Engine;
        if (_soundpadStatus != null && !string.IsNullOrEmpty(e?.PlayingSoundName))
        {
            string playing = "正在播放：" + e.PlayingSoundName;
            if (_soundpadStatus.Text != playing) _soundpadStatus.Text = playing;
        }
        else if (_soundpadStatus?.Text.StartsWith("正在播放：", StringComparison.Ordinal) == true)
            _soundpadStatus.Text = "播放完毕";
        _inputMeter.Value = Level(e?.InputPeak ?? 0);
        _musicMeter.Value = Level(e?.MusicPeak ?? 0);
        _outputMeter.Value = Level(e?.OutputPeak ?? 0);
        SetText(_inDb, Db(e?.InputPeak ?? 0) + " dB");
        SetText(_musicDb, Db(e?.MusicPeak ?? 0) + " dB");
        SetText(_outDb, Db(e?.OutputPeak ?? 0) + " dB");
        string levels = "";
        if (e != null && _controller.Settings.Music)
            levels = e.CapturedPid > 0
                ? T("正在捕获进程 PID ", "Capturing process PID ") + e.CapturedPid
                : T("尚未找到所选软件的音频进程；请先播放音乐再重新选择。", "No audio process found. Play music and select the application again.");
        if (e?.ProcessingTooSlow == true)
            levels += (levels.Length > 0 ? "\n" : "") + T("当前降噪模式处理超时；请在空闲时测试延迟，或手动切换到 Eco。",
                "This mode is missing real-time deadlines. Test latency when idle or switch to Eco.");
        if (!string.IsNullOrEmpty(e?.MonitorError))
            levels += (levels.Length > 0 ? "\n" : "") + T("监听设备错误：", "Monitor device error: ") + e.MonitorError;
        SetText(_levels, levels);
        _levels.Visibility = levels.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        _spectrum.Update(e?.SpectrumFrame ?? new float[480]);
        static double Level(float v) => Math.Clamp(20 * Math.Log10(Math.Max(.000001, v)) + 60, 0, 60);
        static string Db(float v) => v < .00001f ? "−∞" : (20 * Math.Log10(v)).ToString("F1");
        static void SetText(TextBlock block, string text) { if (block != null && block.Text != text) block.Text = text; }
    }

    // ---- dialogs ----------------------------------------------------------

    Window Dialog(string title, Panel panel, double height = 570, double width = 580)
    {
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.Resources[typeof(ScrollBar)] = ThemeTokens.CreateDarkScrollBarStyle();
        var window = OneBoxWindow.Create(_window, title, width, height, scroll, true);
        window.Show(); return window;
    }
    void Guide()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
        var heading = Text(T("三步开始共享", "Start sharing in three steps"), 15); heading.FontWeight = FontWeights.SemiBold; heading.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(heading);
        var steps = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, -8, 10) };
        (string Title, string Body)[] stepTexts =
        {
            ("安装 VB-CABLE", "点击“安装 VB-CABLE 官方驱动”，OneMic 会下载并打开官方安装程序。确认管理员授权，按安装程序提示完成安装并重启。"),
            ("选择设备并共享", "在 OneMic 选择真实麦克风和 CABLE Input，开始共享。音效板可直接导入音频文件。"),
            ("通话软件里切换", "在通话软件中把麦克风设为 CABLE Output，耳机保持原来的输出设备。")
        };
        for (int i = 0; i < stepTexts.Length; i++)
        {
            var card = new StackPanel();
            var badge = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
                Background = B(i == 0 ? ThemeTokens.Accent : ThemeTokens.Active),
                Child = new TextBlock { Text = (i + 1).ToString(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = B(i == 0 ? ThemeTokens.OnAccent : ThemeTokens.PrimaryText), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            card.Children.Add(badge);
            var t = Text(stepTexts[i].Title, 12); t.FontWeight = FontWeights.SemiBold; t.Margin = new Thickness(0, 0, 0, 4);
            card.Children.Add(t);
            card.Children.Add(Text(stepTexts[i].Body, 11, ThemeTokens.SecondaryText));
            var border = CardBorder(card); border.Margin = new Thickness(0, 0, 8, 0); border.VerticalAlignment = VerticalAlignment.Stretch;
            steps.Children.Add(border);
        }
        panel.Children.Add(steps);
        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        buttons.Children.Add(Btn("安装 VB-CABLE 官方驱动", () => _ = VbCableInstaller.InstallAsync(_window), ButtonVariant.Primary, 32));
        buttons.Children.Add(Btn(T("打开 VB-CABLE 官网", "Open VB-CABLE website"), () => Open("https://vb-audio.com/Cable/"), ButtonVariant.Secondary, 32));
        buttons.Children.Add(Btn(T("降噪运行库 · Microsoft 官方", "Denoising runtime · Microsoft official"), () => Open("https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist"), ButtonVariant.Flat, 32));
        panel.Children.Add(buttons);
        var faqTitle = Text(T("常见问题", "FAQ"), 13); faqTitle.FontWeight = FontWeights.SemiBold; faqTitle.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(faqTitle);
        string faq = T("• 朋友听不到音乐：确认 BGM 开关和应用选择；适当关闭聊天软件的降噪、自动增益或启用音乐模式。\n• 软件不在列表：先让它播放声音，再等待列表刷新。浏览器可能包含多个标签页的声音。\n• 听到重音：关闭“最终输出”监听；音乐播放器本身已经在播放。\n• 同一 USB 声卡的麦克风与耳机：若监听出现卡顿，可换独立耳机。\n• RNNoise 适合持续底噪；DeepFilterNet3 更适合复杂噪声，但资源消耗和延迟可能更高。\n• 停止或退出 OneBox 后，通话请切回真实麦克风。关闭本窗口会继续运行；退出托盘才会关闭 OneBox。\n• 炸麦为受限幅保护的失真音效，不会绕过输出限幅。", "• No music: check the BGM switch and app selection. Adjust chat noise suppression, automatic gain, or music mode.\n• App missing: play audio and wait for the list to refresh. Browser capture can include multiple tabs.\n• Doubled music: turn off Final output monitoring; your player is already audible.\n• Mic/headphones on the same USB device: try a separate monitor device if playback stutters.\n• RNNoise suits steady noise; DeepFilterNet3 handles complex noise with potentially higher CPU cost and latency.\n• After stopping or exiting OneBox, switch chat back to your physical microphone. Closing this window keeps sharing active.\n• Distortion remains protected by the output limiter.");
        var faqCard = new StackPanel();
        string[] items = faq.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < items.Length; i++)
        {
            string line = items[i].TrimStart('•', ' ');
            int split = line.IndexOf('：');
            if (split < 0) split = line.IndexOf('；');
            if (split < 0) split = line.IndexOf(':');
            string question = split > 0 ? line.Substring(0, split) : line;
            string answer = split > 0 ? line.Substring(split + 1).Trim() : line;
            if (i > 0) faqCard.Children.Add(UiKit.MakeRowDivider());
            faqCard.Children.Add(FaqItem(question, answer, i == 0));
        }
        var faqBorder = CardBorder(faqCard); faqBorder.Padding = new Thickness(0, 2, 0, 2);
        panel.Children.Add(faqBorder);
        var links = new WrapPanel();
        links.Children.Add(Btn(T("OneBox 项目 / 反馈", "OneBox project / feedback"), () => Open("https://github.com/OneT1er/OneBox"), ButtonVariant.Flat));
        links.Children.Add(Btn("RNNoise", () => Open("https://github.com/xiph/rnnoise"), ButtonVariant.Flat));
        links.Children.Add(Btn("DeepFilterNet3", () => Open("https://github.com/Rikorose/DeepFilterNet"), ButtonVariant.Flat));
        panel.Children.Add(links);
        Dialog(T("使用教程", "Guide"), panel, 660, 600);
    }
    StackPanel FaqItem(string question, string answer, bool expanded)
    {
        var item = new StackPanel();
        var head = new DockPanel { LastChildFill = true };
        var chevron = IconCatalog.CreateElement(expanded ? IconKey.ChevronUp : IconKey.ChevronDown, 12, Muted);
        DockPanel.SetDock(chevron, Dock.Right); head.Children.Add(chevron);
        var q = Text(question, 12); q.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(q);
        var button = new Button { Content = head, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 40, Padding = new Thickness(14, 6, 14, 6) };
        UiKit.ApplyButtonVariant(button, ButtonVariant.Flat);
        AutomationProperties.SetName(button, question);
        var body = Text(answer, 12, ThemeTokens.SecondaryText);
        body.Margin = new Thickness(14, 0, 14, 10);
        body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        button.Click += (_, _) =>
        {
            bool open = body.Visibility != Visibility.Visible;
            body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            head.Children.Remove(chevron);
            chevron = IconCatalog.CreateElement(open ? IconKey.ChevronUp : IconKey.ChevronDown, 12, Muted);
            DockPanel.SetDock(chevron, Dock.Right);
            head.Children.Insert(0, chevron);
        };
        item.Children.Add(button); item.Children.Add(body);
        return item;
    }
    void Equalizer()
    {
        var panel = new StackPanel { Margin = new Thickness(16, 4, 16, 16) };
        panel.Children.Add(Text(T("拖动圆点改变音色；增益范围 −12 到 +12 dB。只影响人声。", "Drag points to shape your voice, from −12 to +12 dB. Music is unaffected."), 11, ThemeTokens.SecondaryText));
        var curve = new StudioEqCurve { Gains = (float[])_controller.Settings.Bands.Clone() };
        var names = new[] { "Flat", "Voice", "Warm", "Bright", "Bass", "Radio", "Presence", "Custom" };
        var labels = new[] { T("平直", "Flat"), T("清晰人声", "Voice"), T("温暖", "Warm"), T("明亮", "Bright"), T("低音", "Bass"), T("电台", "Radio"), T("临场感", "Presence"), T("自定义", "Custom") };
        float[][] presets = { new float[10], new float[] { -6,-4,-2,0,1,2,3,2,0,-2 }, new float[] { 0,2,3,2,0,0,-1,-2,-2,-3 }, new float[] { -3,-2,-1,0,0,1,2,3,3,2 }, new float[] { 5,4,3,1,0,0,0,0,0,0 }, new float[] { -12,-10,-6,1,3,4,3,0,-6,-10 }, new float[] { -4,-3,-2,0,1,3,4,3,1,0 } };
        int current = Math.Max(0, Array.IndexOf(names, _controller.Settings.EqPreset));

        // Preset chips.
        var chips = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        var chipButtons = new Button[labels.Length];
        // Per-band readout cells.
        var readout = new UniformGrid { Columns = 10, Margin = new Thickness(0, 8, 0, 0) };
        var cells = new (Border Cell, TextBlock Gain)[10];
        for (int b = 0; b < 10; b++)
        {
            int band = b;
            float hz = StudioDsp.Frequencies[b];
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = hz >= 1000 ? (hz / 1000).ToString("0") + "k" : hz.ToString("0"), FontSize = 10, Foreground = B(ThemeTokens.TertiaryText), HorizontalAlignment = HorizontalAlignment.Center });
            var gain = new TextBlock { FontSize = 11, Foreground = Foreground, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
            stack.Children.Add(gain);
            var cell = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(0, 4, 0, 4), Margin = new Thickness(1, 0, 1, 0), Child = stack, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            cell.MouseLeftButtonUp += (_, _) => { curve.Select(band); curve.Focus(); };
            cells[b] = (cell, gain);
            readout.Children.Add(cell);
        }
        void UpdateReadout()
        {
            for (int b = 0; b < 10; b++)
            {
                cells[b].Gain.Text = curve.Gains[b].ToString("+0.0;-0.0;0");
                bool selected = b == curve.Selected;
                cells[b].Cell.Background = selected ? B(ThemeTokens.Active) : Brushes.Transparent;
                cells[b].Gain.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        void MarkChip(int index)
        {
            current = index;
            for (int i = 0; i < chipButtons.Length; i++)
            {
                bool on = i == index;
                chipButtons[i].Background = on ? B(ThemeTokens.Active) : Brushes.Transparent;
                chipButtons[i].BorderBrush = B(on ? ThemeTokens.Accent : ThemeTokens.BorderStrong);
                chipButtons[i].Foreground = B(on ? ThemeTokens.PrimaryText : ThemeTokens.SecondaryText);
            }
        }
        void Pick(int index)
        {
            curve.Gains = (float[])(index >= 7 ? _controller.Settings.CustomBands : presets[index]).Clone(); curve.InvalidateVisual();
            MarkChip(index); UpdateReadout();
            Change(s => { s.Bands = (float[])curve.Gains.Clone(); s.EqPreset = names[index]; s.Eq = true; });
        }
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var chip = new Button { Content = labels[i], Height = 28, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(12, 0, 12, 0) };
            UiKit.ApplyButtonVariant(chip, ButtonVariant.Secondary);
            chip.Click += (_, _) => Guard(() => Pick(index));
            chipButtons[i] = chip;
            chips.Children.Add(chip);
        }
        MarkChip(current);
        panel.Children.Add(chips);

        curve.Changed += () =>
        {
            Change(s => { s.Bands = (float[])curve.Gains.Clone(); s.CustomBands = (float[])curve.Gains.Clone(); s.EqPreset = "Custom"; s.Eq = true; });
            MarkChip(7); UpdateReadout();
        };
        curve.SelectionChanged += UpdateReadout;
        var graph = new StackPanel();
        graph.Children.Add(curve);
        graph.Children.Add(readout);
        var graphCard = new Border { Background = B(ThemeTokens.Background), BorderBrush = B(ThemeTokens.Border), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(8), Child = graph };
        panel.Children.Add(graphCard);
        UpdateReadout();

        // Footer: EQ switch on the left, reset / close on the right.
        var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 12, 0, 0) };
        Window dialog = null;
        var close = Btn(T("关闭", "Close"), () => dialog?.Close(), ButtonVariant.Primary, 32); close.Margin = new Thickness(0); close.Width = 80;
        DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var reset = Btn(T("恢复平直", "Reset to flat"), () => Pick(0), ButtonVariant.Secondary, 32);
        DockPanel.SetDock(reset, Dock.Right); footer.Children.Add(reset);
        var enable = Switch(T("启用 10 段均衡器", "Enable 10-band EQ"), x => x.Eq, (x, v) => x.Eq = v);
        enable.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(enable);
        panel.Children.Add(footer);
        dialog = Dialog(T("均衡器", "Equalizer"), panel, 540, 600);
        // The switch is registered in _toggles for Sync(); drop it when the
        // dialog goes away so a closed dialog is not kept alive.
        dialog.Closed += (_, _) => _toggles.RemoveAll(t => ReferenceEquals(t.Box, enable));
    }
    static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
