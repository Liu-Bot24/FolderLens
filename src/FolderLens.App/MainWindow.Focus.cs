using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool maximizeOnActivation;
    private bool FocusIfForeground(UIElement element,FocusState state)=>WindowFocus.Focus(this,element,state);
    internal void ShowAtStartup(long requestedForeground)
    {
        // Maximize() can activate a hidden window. Defer it until our window owns focus.
        maximizeOnActivation=true;
        Activated+=(_,args)=>
        {
            if(args.WindowActivationState==WindowActivationState.Deactivated||!maximizeOnActivation||!WindowFocus.IsForeground(this))return;
            maximizeOnActivation=false;
            if(AppWindow.Presenter is OverlappedPresenter presenter)presenter.Maximize();
        };
        WindowFocus.Show(this,requestedForeground);
    }
}
