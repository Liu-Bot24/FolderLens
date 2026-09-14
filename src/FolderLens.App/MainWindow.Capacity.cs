using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private CapacityWindow? capacityWindow;
    private bool openingCapacity;
    private Task capacityOpening=Task.CompletedTask;
    private void ShowCapacity(object sender,RoutedEventArgs args)
    {
        if(closing||openingCapacity||catalog is null||rootId.Length==0||activeCollectionId is not null)return;
        if(capacityWindow is not null){if(capacityWindow.IsClosing)Status.Text="正在关闭容量看板，请稍后打开。";else capacityWindow.Activate();return;}
        openingCapacity=true;capacityOpening=OpenCapacityWindow();
    }
    private async Task OpenCapacityWindow()
    {
        long requestedForeground=WindowFocus.Foreground;
        var store=catalog!;string capturedRoot=rootId,capturedPath=root;
        ResultHandle? handle=resultHandle is {IsPendingView:false}?resultHandle:null;bool retained=false;
        try
        {
            if(handle is not null){retained=await store.RetainSnapshot(handle.Id,lifetime.Token);if(!retained)handle=null;}
            if(closing)return;
            var window=new CapacityWindow(store,capturedRoot,capturedPath,handle,lifetime.Token,(path,direct)=>BrowseCapacityDirectory(capturedRoot,path,direct));capacityWindow=window;retained=false;
            window.Closed+=async(_,_)=>
            {
                try{await window.ShutdownAsync();}catch(Exception error){ShowError(error);}
                finally{if(ReferenceEquals(capacityWindow,window))capacityWindow=null;}
            };
            WindowFocus.Show(window,requestedForeground);
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){ShowError(error);}
        finally{openingCapacity=false;if(retained&&handle is not null)try{await store.ReleaseSnapshot(handle.Id);}catch(Exception error){ShowError(error);}}
    }
    private async Task CloseCapacityWindow()
    {
        await capacityOpening;
        if(capacityWindow is not {} window)return;
        if(!window.IsClosing)window.Close();await window.ShutdownAsync();if(ReferenceEquals(capacityWindow,window))capacityWindow=null;
    }
}
