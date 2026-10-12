using FolderLens.Core;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed class PendingFileOpen(string path,long interaction,CancellationTokenSource cancellation)
    {
        public string Path {get;}=path;
        public long Interaction {get;}=interaction;
        public CancellationTokenSource Cancellation {get;}=cancellation;
        public long RootVersion {get;set;}=-1;
        public long Navigation {get;set;}=-1;
        public Task? LocationTask {get;set;}
    }
    private PendingFileOpen? pendingFileOpen;
    private void CancelPendingFileOpen()
    {
        var pending=pendingFileOpen;pendingFileOpen=null;
        pending?.Cancellation.Cancel();
    }
    private bool OwnsFileOpen(PendingFileOpen request)=>!closing&&ReferenceEquals(pendingFileOpen,request)
        &&!request.Cancellation.IsCancellationRequested&&request.Interaction==browserInteractionVersion
        &&(request.RootVersion<0||request.RootVersion==rootChangeVersion&&request.Navigation==directoryNavigationRequest);

    private async Task OpenPath(string path)
    {
        CancelPendingFileOpen();path=Path.GetFullPath(path);
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var request=new PendingFileOpen(path,browserInteractionVersion,cancellation);pendingFileOpen=request;
        try
        {
            bool directory=await prefetchSourceProbe.IsDirectory(path,cancellation.Token);
            if(!OwnsFileOpen(request))return;
            if(directory){pendingFileOpen=null;await OpenRoot(path);return;}
            string parent=Path.GetDirectoryName(path)??throw new ArgumentException("文件路径无效。");
            // Explicit file activation starts a neutral browser query. Keep the
            // user's hidden-file choice; never reveal every hidden file silently.
            var filter=new FilterSpec{RootId=rootId.Length>0?rootId:"file-open",Kinds=[],ShowHidden=ShowHidden.IsChecked==true,Grouping=folderGrouping};
            var view=new SavedView(parent,filter,null,0,DetailsMode.IsChecked==true);
            await OpenRoot(parent,preserveDirectoryScope:true,restoredView:view,fileOpen:request,
                canAccept:()=>OwnsFileOpen(request),onAccepted:()=>{request.RootVersion=rootChangeVersion;request.Navigation=directoryNavigationRequest;});
            await TryConsumeFileOpen();
            if(OwnsFileOpen(request)&&request.RootVersion>=0)
            {
                pendingFileOpen=null;
                string message=browserScanError is not null?"当前已加载的文件中没有找到指定文件，请先处理文件夹扫描错误。":
                    "没有在当前列表中找到指定文件。它可能已移动、无法访问，或被“显示隐藏文件”设置排除。";
                FileTitle.Text=Path.GetFileName(path);QualityLabel.Text=message;Status.Text=message;
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){}
        finally{if(ReferenceEquals(pendingFileOpen,request))pendingFileOpen=null;}
    }
    private Task TryConsumeFileOpen()
    {
        if(pendingFileOpen is not {} request||request.RootVersion<0||!OwnsFileOpen(request)||catalog is null)return Task.CompletedTask;
        return request.LocationTask is {IsCompleted:false} active?active:request.LocationTask=ConsumeFileOpen(request);
    }
    private async Task ConsumeFileOpen(PendingFileOpen request)
    {
        try
        {
            string relative=Path.GetRelativePath(root,request.Path);
            if(resultHandle is {} handle&&results is {} source)
            {
                var token=request.Cancellation.Token;
                long? ordinal=await catalog!.FindOrdinal(handle.Id,relative,token);
                if(!OwnsFileOpen(request)||!ReferenceEquals(resultHandle,handle)||!ReferenceEquals(results,source))return;
                if(ordinal is null)return;
                // Keep ownership and its cancellation through the target page
                // read. Detach only at the synchronous selection commit point.
                await SelectBrowserOrdinal(source,checked((int)ordinal.Value),token,request);
            }
            else if(firstPageSequence.FirstOrDefault(row=>row.RelativePath==relative) is {} row)
            {
                pendingFileOpen=null;BeginBrowserMovement();
                bool prior=syncingBrowserSelection;syncingBrowserSelection=true;
                try{ActiveBrowser.SelectedItem=row;ActiveBrowser.ScrollIntoView(row,ScrollIntoViewAlignment.Leading);}
                finally{syncingBrowserSelection=prior;}
                await SelectPreview(row);
            }
        }
        catch(OperationCanceledException){}
        catch(Exception error){if(OwnsFileOpen(request)){pendingFileOpen=null;ShowError(error);}}
    }
}
