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
        if(capacityWindow is not null){if(capacityWindow.IsClosing)Status.Text="正在关闭目录容量窗口，请稍后重试。";else capacityWindow.Activate();return;}
        openingCapacity=true;capacityOpening=OpenCapacityWindow();
    }
    private Task OpenCapacityWindow()
    {
        long requestedForeground=WindowFocus.Foreground;
        var store=catalog!;string capturedRoot=rootId,capturedPath=root;
        try
        {
            if(closing)return Task.CompletedTask;
            var window=new CapacityWindow(store,capturedRoot,capturedPath,lifetime.Token,(path,direct)=>BrowseCapacityDirectory(capturedRoot,path,direct),
                currentObservationsOnly:true,physicalCapacity:true,exclusions:ScanExclusions(),scanWorkerExecutable:ScanWorkerClient.FindExecutable(ScanWorkerDirectory));capacityWindow=window;
            window.Closed+=async(_,_)=>
            {
                try{await window.ShutdownAsync();}catch(Exception error){ShowError(error);}
                finally{if(ReferenceEquals(capacityWindow,window))capacityWindow=null;}
            };
            WindowFocus.Show(window,requestedForeground);
        }
        catch(Exception error){ShowError(error);}
        finally{openingCapacity=false;}
        return Task.CompletedTask;
    }
    private async Task CloseCapacityWindow()
    {
        await capacityOpening;
        if(capacityWindow is not {} window)return;
        if(!window.IsClosing)window.Close();await window.ShutdownAsync();if(ReferenceEquals(capacityWindow,window))capacityWindow=null;
    }
}
