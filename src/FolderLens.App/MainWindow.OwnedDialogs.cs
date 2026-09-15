using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;
public sealed partial class MainWindow
{
    // Register before presentation so shutdown can always finish the ShowAsync wait.
    // Called directly from user commands; no delayed background activation.
    private async Task<ContentDialogResult> ShowOwnedDialog(ContentDialog dialog)
    {
        lifetime.Token.ThrowIfCancellationRequested();
        if(verifyOwnedDialogOpened is not null)dialog.Opened+=(_,_)=>verifyOwnedDialogOpened?.Invoke(dialog);
        using var cancel=lifetime.Token.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog.Hide()));
        return await dialog.ShowAsync();
    }
}
