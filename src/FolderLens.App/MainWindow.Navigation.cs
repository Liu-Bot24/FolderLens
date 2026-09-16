using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private readonly NavigationHistory<SavedView> navigationHistory=new();
    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled=immersive||fullScreen||navigationHistory.CanGoBack;
        ForwardButton.IsEnabled=navigationHistory.CanGoForward;
    }
    private async Task NavigateHistory(bool forward)
    {
        if(!forward&&(immersive||fullScreen)){await ReturnToBrowser();UpdateNavigationButtons();return;}
        if(forward?!navigationHistory.CanGoForward:!navigationHistory.CanGoBack)return;
        await ReturnToBrowser();
        var target=forward?navigationHistory.GoForward(CaptureView()):navigationHistory.GoBack(CaptureView());
        UpdateNavigationButtons();await RestoreSavedView(target,false,recheckDirectory:true);
    }
    private async void BackFolder(object sender,RoutedEventArgs e){try{await NavigateHistory(false);}catch(Exception ex){ShowError(ex);}}
    private async void ForwardFolder(object sender,RoutedEventArgs e){try{await NavigateHistory(true);}catch(Exception ex){ShowError(ex);}}
}
