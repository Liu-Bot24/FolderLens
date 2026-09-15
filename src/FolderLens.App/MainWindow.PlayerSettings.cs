using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool internalVideoByDefault;
    private async Task RestorePlayerPreferences()
    {
        var current = await settings!.Load<PlayerPreferences>("player-preferences.json")
            ?? new(await settings.Load<string>("player.json"), await settings.Load<bool>("player-playlists.json"));
        ApplyPlayerPreferences(current);
    }

    private void ApplyPlayerPreferences(PlayerPreferences value)
    {
        playerExecutable = string.IsNullOrWhiteSpace(value.Executable) ? null : value.Executable;
        playerSupportsPlaylists = playerExecutable is not null && value.SupportsPlaylists;
        internalVideoByDefault=value.InternalVideo;
        UpdatePlaylistCommand();
    }

    private async Task SavePlayerPreferences(PlayerPreferences value, CancellationToken cancellation)
    {
        await settings!.Save("player-preferences.json", value, cancellation);
        if (!closing) { ApplyPlayerPreferences(value); Status.Text = "播放器设置已保存。"; }
    }

    private async void ConfigurePlayer(object sender, RoutedEventArgs e)
    {
        using var operation = browserWork.Enter(); if (operation is null || closing) return;
        var dialog = new PlayerSettingsDialog(new(playerExecutable, playerSupportsPlaylists,internalVideoByDefault), async () =>
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            return (await picker.PickSingleFileAsync())?.Path;
        }, SavePlayerPreferences, lifetime.Token) { XamlRoot = Shell.XamlRoot };
        using var registration = lifetime.Token.Register(() => DispatcherQueue.TryEnqueue(() => { dialog.CancelPending(); dialog.Hide(); }));
        try { await dialog.ShowAsync(); } catch (Exception ex) { if (!closing) ShowError(ex); }
        finally { await dialog.Retire(); }
    }
}
