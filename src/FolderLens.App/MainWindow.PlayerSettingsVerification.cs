using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyPlayerSettings(Dictionary<string, object> report)
    {
        if (Environment.GetCommandLineArgs().Contains("--verify-player-admission")) { await PlayerSettingsDialog.VerifyDiscoveryAdmission(report); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-player-discovery")) { await VerifyPlayerDiscovery(report); return; }
        if (Environment.GetCommandLineArgs().Contains("--verify-player-cancel")) { await VerifyPlayerCancellation(report); return; }
        var dialog = new PlayerSettingsDialog(new(), () => Task.FromResult<string?>(null), SavePlayerPreferences) { XamlRoot = Shell.XamlRoot };
        var shown = dialog.ShowAsync();
        try
        {
            for (int i = 0; i < 60 && !dialog.IsLoaded; i++) await Task.Delay(25);
            if (!dialog.IsLoaded) throw new InvalidOperationException("播放器设置未打开。");
            dialog.Mode.SelectedIndex = 1; dialog.PathInput.Text = "missing-player.exe";
            if (await dialog.SaveDraft() || !dialog.Error.IsOpen || shown.Status != Windows.Foundation.AsyncStatus.Started)
                throw new InvalidOperationException("无效路径没有保留设置窗口和错误说明。");
            dialog.PathInput.Text = Environment.ProcessPath!; dialog.Playlists.IsChecked = true;
            if (!await dialog.SaveDraft() || playerExecutable != Environment.ProcessPath || !PlaylistMenu.IsEnabled)
                throw new InvalidOperationException("有效 EXE 没有保存或启用播放列表。");
            await RestoreDesktop();
            if (playerExecutable != Environment.ProcessPath || !playerSupportsPlaylists) throw new InvalidOperationException("播放器设置没有持久保存。");
            dialog.UpdateLayout();
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(dialog);
            using var file = File.Create(Path.Combine(dataDirectory, "player-settings.png")); using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray()); await encoder.FlushAsync();
            dialog.Mode.SelectedIndex = 0;
            if (!await dialog.SaveDraft() || playerExecutable is not null || PlaylistMenu.IsEnabled) throw new InvalidOperationException("系统默认播放器没有关闭列表命令。");
            await RestoreDesktop();
            if (playerExecutable is not null || playerSupportsPlaylists) throw new InvalidOperationException("系统默认选择没有持久保存。");
        }
        finally { dialog.Hide(); await shown; await dialog.Retire(); }
        var originalSettings = settings;
        try
        {
            string blocked = Path.Combine(dataDirectory, "blocked-settings"); await File.WriteAllTextAsync(blocked, "not a directory");
            settings = new FolderLens.Infrastructure.AtomicSettings(blocked);
            var failure = new PlayerSettingsDialog(new(Environment.ProcessPath, true), () => Task.FromResult<string?>(null), SavePlayerPreferences);
            if (await failure.SaveDraft() || !failure.Error.IsOpen || playerExecutable is not null || PlaylistMenu.IsEnabled)
                throw new InvalidOperationException("保存失败没有显示错误或改变了现有设置。");
            await failure.Retire();
            settings = new FolderLens.Infrastructure.AtomicSettings(Path.Combine(dataDirectory, "legacy-player"));
            await settings.Save("player.json", Environment.ProcessPath); await settings.Save("player-playlists.json", true);
            await RestorePlayerPreferences();
            if (playerExecutable != Environment.ProcessPath || !PlaylistMenu.IsEnabled) throw new InvalidOperationException("旧播放器设置未恢复。");
        }
        finally { settings = originalSettings; await RestorePlayerPreferences(); }
        report["invalidPathRetainedDialog"] = true; report["persistedCustomAndSystem"] = true;
        report["saveFailureReported"] = true; report["status"] = "PASS";
    }

    private async Task VerifyPlayerCancellation(Dictionary<string, object> report)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool committed = false;
        var dialog = new PlayerSettingsDialog(new(), () => Task.FromResult<string?>(null), async (_, cancellation) => { entered.TrySetResult(); await release.Task.WaitAsync(cancellation); cancellation.ThrowIfCancellationRequested(); committed = true; }) { XamlRoot = Shell.XamlRoot };
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        var shown = dialog.ShowAsync(); bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        dialog.CloseButtonClick += (_, _) => report["closeButtonReached"] = true;
        dialog.Closing += (_, _) => report["closingReached"] = true;
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(2));
            dialog.UpdateLayout();
            void Invoke(string name) { report[name + "Enabled"] = dialog.InvokeTemplateButton(name); }
            Invoke("PrimaryButton"); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await Task.Delay(50);
            Invoke("CloseButton");
            for (int i = 0; i < 20 && !closed; i++) await Task.Delay(25);
            report["cancelClosedWhileSaving"] = closed;
            if (!closed) throw new InvalidOperationException("原生取消按钮在异步保存期间没有关闭对话框。");
            await dialog.ActiveSave.WaitAsync(TimeSpan.FromSeconds(2));
            if (committed) throw new InvalidOperationException("取消后仍然提交了设置。");
            report["cancelPreventedCommit"] = true;
            report["status"] = "PASS";
        }
        finally { release.TrySetResult(); await Task.Delay(50); dialog.Hide(); await shown; await dialog.Retire(); }
        var success = new PlayerSettingsDialog(new(), () => Task.FromResult<string?>(null), SavePlayerPreferences) { XamlRoot = Shell.XamlRoot };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        success.Opened += (_, _) => ready.TrySetResult();
        var successShown = success.ShowAsync();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
            success.InvokeTemplateButton("PrimaryButton");
            for (int i = 0; i < 80 && successShown.Status == Windows.Foundation.AsyncStatus.Started; i++) await Task.Delay(25);
            if (successShown.Status != Windows.Foundation.AsyncStatus.Completed || playerExecutable is not null)
                throw new InvalidOperationException("真实保存按钮成功后没有关闭窗口或更新设置。");
            report["nativeSaveClosed"] = true;
        }
        finally { success.Hide(); await successShown; await success.Retire(); }
    }

    private async Task VerifyPlayerDiscovery(Dictionary<string, object> report)
    {
        int saves = 0;
        var dialog = new PlayerSettingsDialog(new(), () => Task.FromResult<string?>(null), (_, _) => { saves++; return Task.CompletedTask; }) { XamlRoot = Shell.XamlRoot };
        var shown = dialog.ShowAsync();
        try
        {
            for (int i = 0; i < 60 && !dialog.IsLoaded; i++) await Task.Delay(25);
            await dialog.VerifyDetectedChoice(report);
            if (saves != 0) throw new InvalidOperationException("检测或选择播放器自动保存了配置。");
            dialog.UpdateLayout();
            var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(dialog);
            using var file = File.Create(Path.Combine(dataDirectory, "detected-player.png")); using var stream = file.AsRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray()); await encoder.FlushAsync();
            report["status"] = "PASS";
        }
        finally { dialog.Hide(); await shown; await dialog.Retire(); }
    }
}

internal sealed partial class PlayerSettingsDialog
{
    private static Func<string, bool>? verifyPathExists;
    internal static async Task VerifyDiscoveryAdmission(Dictionary<string, object> report)
    {
        using var release = new ManualResetEventSlim();
        using var firstStop = new CancellationTokenSource(); using var secondStop = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int entered = 0; bool saved = false;
        verifyPathExists = path =>
        {
            if (path != "blocked-discovery") return File.Exists(path);
            if (Interlocked.Increment(ref entered) == 1) firstEntered.TrySetResult(); else secondEntered.TrySetResult();
            release.Wait(); return false;
        };
        Task<bool>? first = null, second = null;
        var manual = new PlayerSettingsDialog(new(Environment.ProcessPath), () => Task.FromResult<string?>(null), (_, _) => { saved = true; return Task.CompletedTask; });
        try
        {
            first = Exists("blocked-discovery", firstStop.Token, automatic: true);
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            firstStop.Cancel(); try { await first; } catch (OperationCanceledException) { }
            second = Exists("blocked-discovery", secondStop.Token, automatic: true);
            await Task.WhenAny(secondEntered.Task, Task.Delay(300));
            report["automaticChecksBeforeManualSave"] = entered;
            if (!await manual.SaveDraft().WaitAsync(TimeSpan.FromSeconds(1)) || !saved)
                throw new InvalidOperationException("自动检测占用全部名额，阻止手动保存本地播放器。");
            report["unfinishedAutomaticChecks"] = entered;
            if (entered != 1) throw new InvalidOperationException("自动检测启动了多个尚未结束的 OS 查询。");
            report["manualSaveWhileDiscoveryBlocked"] = true; report["status"] = "PASS";
        }
        finally
        {
            manual.CancelPending(); firstStop.Cancel(); secondStop.Cancel(); release.Set();
            await manual.Retire();
            foreach (var task in new[] { first, second }) if (task is not null) try { await task; } catch (OperationCanceledException) { }
            for (int i = 0; i < 80 && pathChecks.CurrentCount != 2; i++) await Task.Delay(25);
            verifyPathExists = null;
            if (pathChecks.CurrentCount != 2 || automaticPathChecks.CurrentCount != 1) throw new InvalidOperationException("实际查询完成后没有归还全部名额。");
        }
    }
    internal async Task VerifyDetectedChoice(Dictionary<string, object> report)
    {
        Mode.SelectedIndex = 1; PathInput.Text = @"C:\typed-player.exe";
        await discovery;
        if (PathInput.Text != @"C:\typed-player.exe") throw new InvalidOperationException("检测覆盖了用户输入。");
        var buttons = detected.Children.OfType<Button>().ToArray(); report["detectedCount"] = buttons.Length;
        if (buttons.Length == 0) throw new InvalidOperationException("本机已登记的播放器没有显示。");
        Playlists.IsChecked = true;
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(buttons[0]);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Delay(30);
        if (Mode.SelectedIndex != 1 || !File.Exists(PathInput.Text) || Playlists.IsChecked == true)
            throw new InvalidOperationException("选择已发现播放器未正确填入路径或沿用了旧播放列表声明。");
        report["discoveryPreservedDraft"] = true; report["choiceRequiresSave"] = true;
    }
    internal bool InvokeTemplateButton(string name)
    {
        ApplyTemplate(); UpdateLayout();
        var button = GetTemplateChild(name) as Button ?? throw new InvalidOperationException("未找到对话框模板按钮：" + name);
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        return button.IsEnabled;
    }
}
