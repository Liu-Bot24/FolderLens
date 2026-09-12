using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

internal sealed record PlayerPreferences(string? Executable = null, bool SupportsPlaylists = false);

internal sealed partial class PlayerSettingsDialog : ContentDialog
{
    private static readonly SemaphoreSlim pathChecks = new(2, 2);
    private static readonly SemaphoreSlim automaticPathChecks = new(1, 1);
    internal readonly ComboBox Mode = new() { Header = "打开视频的方式", ItemsSource = new[] { "系统默认播放器", "指定播放器" }, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal readonly TextBox PathInput = new() { Header = "播放器程序", PlaceholderText = "选择播放器的 .exe 文件" };
    internal readonly CheckBox Playlists = new() { Content = "此播放器支持 M3U8 播放列表" };
    internal readonly InfoBar Error = new() { Severity = InfoBarSeverity.Error, IsClosable = false };
    private readonly StackPanel custom = new() { Spacing = 12 };
    private readonly Button choose = new() { Content = "浏览…", HorizontalAlignment = HorizontalAlignment.Right };
    private readonly StackPanel detected = new() { Spacing = 8 };
    private readonly TextBlock discoveryState = new() { Text = "正在检测已安装的播放器…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Func<PlayerPreferences, CancellationToken, Task> save;
    private readonly CancellationTokenSource stop;
    private bool saving, closed;
    private Task discovery = Task.CompletedTask;
    private bool discoveryStarted;
    internal Task<bool> ActiveSave { get; private set; } = Task.FromResult(false);

    internal PlayerSettingsDialog(PlayerPreferences current, Func<Task<string?>> pick, Func<PlayerPreferences, CancellationToken, Task> save, CancellationToken cancellation = default)
    {
        this.save = save; stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Title = "视频播放器"; PrimaryButtonText = "保存"; CloseButtonText = "取消"; DefaultButton = ContentDialogButton.Primary;
        var panel = new StackPanel { Spacing = 20, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = "在 FolderLens 中查看视频封面，使用本地播放器播放。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Mode);
        detected.Children.Add(discoveryState); panel.Children.Add(detected);
        custom.Children.Add(PathInput); custom.Children.Add(choose); custom.Children.Add(Playlists); panel.Children.Add(custom);
        panel.Children.Add(new TextBlock { Text = "系统默认播放器用于打开单个视频。指定支持播放列表的播放器后，可以按当前筛选顺序播放多个视频。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Error);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        PathInput.Text = current.Executable ?? ""; Playlists.IsChecked = current.SupportsPlaylists;
        Mode.SelectionChanged += (_, _) => { custom.Visibility = Mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; Error.IsOpen = false; };
        Mode.SelectedIndex = string.IsNullOrWhiteSpace(current.Executable) ? 0 : 1;
        PathInput.TextChanged += (_, _) => Error.IsOpen = false;
        foreach (var (element, id, name) in new (FrameworkElement, string, string)[] {
            (Mode, "PlayerMode", "打开视频的方式"), (PathInput, "PlayerExecutable", "播放器程序路径"),
            (choose, "ChoosePlayer", "浏览并选择播放器程序"), (Playlists, "PlayerPlaylists", "此播放器支持 M3U8 播放列表") })
        { AutomationProperties.SetAutomationId(element, id); AutomationProperties.SetName(element, name); }
        ToolTipService.SetToolTip(choose, "选择本地播放器的 EXE 文件");
        choose.Click += async (_, _) =>
        {
            choose.IsEnabled = false;
            try { if (await pick() is { } path && !closed) PathInput.Text = path; }
            catch (Exception ex) { if (!closed) ShowError(ex.Message); }
            finally { if (!closed) choose.IsEnabled = true; }
        };
        PrimaryButtonClick += async (_, args) =>
        {
            // Complete the native button event immediately so Close/Escape remain usable.
            args.Cancel = true;
            if (await SaveDraft() && !closed) Hide();
        };
        Closing += (_, _) => CancelPending();
        Loaded += (_, _) => { if (!closed && !discoveryStarted) { discoveryStarted = true; discovery = DiscoverPlayers(); } };
    }

    internal Task<bool> SaveDraft() => saving || closed ? Task.FromResult(false) : ActiveSave = SaveCore();

    internal void CancelPending() { if (closed) return; closed = true; stop.Cancel(); }
    internal async Task Retire() { CancelPending(); await ActiveSave; await discovery; stop.Dispose(); }

    private async Task<bool> SaveCore()
    {
        saving = true; IsPrimaryButtonEnabled = false; Mode.IsEnabled = PathInput.IsEnabled = Playlists.IsEnabled = choose.IsEnabled = false;
        foreach (var button in detected.Children.OfType<Button>()) button.IsEnabled = false;
        try
        {
            string? path = Mode.SelectedIndex == 1 ? PathInput.Text.Trim() : null;
            if (path is not null && (!System.IO.Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !await Exists(path, stop.Token)))
                throw new InvalidDataException("请选择存在的播放器 EXE 文件，或切换为系统默认播放器。");
            stop.Token.ThrowIfCancellationRequested();
            await save(new(path, path is not null && Playlists.IsChecked == true), stop.Token);
            if (closed) return false;
            Error.IsOpen = false; return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { return false; }
        catch (Exception ex) { if (!closed) ShowError(ex is TimeoutException ? "检查播放器路径超时，请确认该位置可访问。" : ex.Message); return false; }
        finally { saving = false; if (!closed) { IsPrimaryButtonEnabled = true; Mode.IsEnabled = PathInput.IsEnabled = Playlists.IsEnabled = choose.IsEnabled = true; foreach (var button in detected.Children.OfType<Button>()) button.IsEnabled = true; } }
    }

    private void ShowError(string message) { Error.Title = "设置未保存"; Error.Message = message; Error.IsOpen = true; }

    private async Task DiscoverPlayers()
    {
        int found = 0;
        try
        {
            var candidates = await Task.Run(() => FolderLens.Infrastructure.PlayerDiscovery.ReadCandidates(stop.Token), stop.Token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); deadline.CancelAfter(TimeSpan.FromSeconds(8));
            foreach (string path in candidates.Paths)
            {
                if (!await Exists(path, deadline.Token, automatic: true)) continue;
                if (closed) return;
                var button = new Button { Content = Path.GetFileName(path).Equals("PotPlayerMini64.exe", StringComparison.OrdinalIgnoreCase) ? "使用 PotPlayer（64 位）" : "使用 PotPlayer（32 位）", HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !saving };
                ToolTipService.SetToolTip(button, path);
                AutomationProperties.SetAutomationId(button, "DetectedPlayer" + found);
                AutomationProperties.SetName(button, button.Content + "，" + path);
                button.Click += (_, _) => { if (saving || closed) return; Mode.SelectedIndex = 1; PathInput.Text = path; Playlists.IsChecked = false; };
                detected.Children.Add(button); found++;
            }
            if (!closed) discoveryState.Text = candidates.Incomplete ? "部分安装信息无法读取，仍可手动选择播放器。" : found > 0 ? "检测到已安装的播放器；点击使用，保存后生效。" : "未检测到 PotPlayer，可使用系统默认或手动选择播放器。";
        }
        catch (OperationCanceledException) { if (!closed) discoveryState.Text = "检测已停止，仍可手动选择播放器。"; }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        { if (!closed) discoveryState.Text = "播放器检测未完成，仍可手动选择。"; }
    }

    private static async Task<bool> Exists(string path, CancellationToken cancellation, bool automatic = false)
    {
        bool automaticSlot = false, totalSlot = false, dispatched = false;
        try
        {
            // Reserve capacity for a manual save even when cancelled discovery reads are still blocked.
            if (automatic)
            {
                if (!await automaticPathChecks.WaitAsync(TimeSpan.FromSeconds(5), cancellation)) throw new TimeoutException();
                automaticSlot = true;
            }
            if (!await pathChecks.WaitAsync(TimeSpan.FromSeconds(5), cancellation)) throw new TimeoutException();
            totalSlot = true;
            // An OS metadata read may outlive cancellation; it owns both slots until it really ends.
            var check = Task.Run(() =>
            {
                try { return verifyPathExists is { } probe ? probe(path) : File.Exists(path); }
                finally { pathChecks.Release(); if (automatic) automaticPathChecks.Release(); }
            });
            dispatched = true;
            return await check.WaitAsync(TimeSpan.FromSeconds(5), cancellation);
        }
        finally
        {
            if (!dispatched) { if (totalSlot) pathChecks.Release(); if (automaticSlot) automaticPathChecks.Release(); }
        }
    }
}

