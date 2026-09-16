using System.Runtime.InteropServices;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

public enum ShellFileAction { Copy, Move, Rename, Recycle }
public enum ShellItemOutcome { Completed, SourceRetained, NotCompleted, Indeterminate }
public sealed record ShellFileRequest(string Source,ShellFileAction Action,string? Destination=null,string? NewName=null,SourceFileStamp? Stamp=null,string? Identity=null);
public sealed record ShellFileResult(ShellFileRequest Request,ShellItemOutcome Outcome,int HResult,string? ActualDestination);
public sealed record ShellBatchResult(IReadOnlyList<ShellFileResult> Items,bool Aborted,int HResult);

/// <summary>Windows Shell owns copying, conflict dialogs, progress, cancellation and undo.
/// The adapter only queues operations and records per-item completion; it never implements a second copy/delete pass.</summary>
public static class ShellFileOperations
{
    public static Task<ShellBatchResult> Execute(IReadOnlyList<ShellFileRequest> requests,nint owner=0,CancellationToken cancellation=default)
        =>ExecuteCore(requests,owner,cancellation,false);
    internal static Task<ShellBatchResult> ExecuteCore(IReadOnlyList<ShellFileRequest> requests,nint owner,CancellationToken cancellation,bool noUi)
    {
        // Reject before starting an STA or allocating any Shell COM objects.
        cancellation.ThrowIfCancellationRequested();
        var budget=new FileTransferBudget(requests.Count);
        foreach(var request in requests){cancellation.ThrowIfCancellationRequested();budget.Add(request.Source,request.Destination,request.NewName);}
        requests=requests.ToArray();
        var done=new TaskCompletionSource<ShellBatchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            IFileOperation? operation=null;var objects=new List<object>();var sinks=new List<Sink>();ShellBatchResult? batch=null;Exception? failure=null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                operation=(IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"),true)!)!;
                operation.SetOwnerWindow(owner);
                // Default confirmation/error UI remains enabled. Silent mode is confined to generated-file tests.
                bool duplicateHere=requests.Count>0&&requests.All(r=>r.Action==ShellFileAction.Copy&&SameDirectory(Path.GetDirectoryName(r.Source)!,r.Destination!));
                operation.SetOperationFlags(0x40|0x200|0x20000000|(duplicateHere?0x8u:0)|(noUi?0x4u|0x10u|0x400u:0));
                if(requests.Count>0){var control=new Sink(requests[0],cancellation,true);sinks.Add(control);operation.Advise(control,out _);}
                foreach(var request in requests)
                {
                    cancellation.ThrowIfCancellationRequested();Validate(request);
                    var source=Item(request.Source);objects.Add(source);
                    var sink=new Sink(request,cancellation);sinks.Add(sink);
                    IShellItem? destination=null;
                    if(request.Action is ShellFileAction.Copy or ShellFileAction.Move)
                    {destination=Item(request.Destination??throw new ArgumentException("请选择目标文件夹。"));objects.Add(destination);}
                    if(noUi&&request.Action!=ShellFileAction.Recycle)
                    {
                        string target=request.Action==ShellFileAction.Rename?FileOperations.RenameTarget(request.Source,request.NewName!):Path.Combine(request.Destination!,request.NewName??Path.GetFileName(request.Source));
                        if((File.Exists(target)||Directory.Exists(target))&&!SameRenameEntry(request,target)&&!duplicateHere)throw new IOException("目标位置已有同名文件。");
                    }
                    switch(request.Action)
                    {
                        case ShellFileAction.Copy:operation.CopyItem(source,destination!,request.NewName,sink);break;
                        case ShellFileAction.Move:operation.MoveItem(source,destination!,request.NewName,sink);break;
                        case ShellFileAction.Rename:operation.RenameItem(source,request.NewName!,sink);break;
                        case ShellFileAction.Recycle:operation.DeleteItem(source,sink);break;
                    }
                }
                int hr=operation.PerformOperations();operation.GetAnyOperationsAborted(out bool aborted);
                batch=new(sinks.Where(s=>!s.Control).Select(s=>s.Result??new(s.Request,ShellItemOutcome.NotCompleted,hr,null)).ToArray(),aborted,hr);
            }
            catch(Exception ex){failure=ex;}
            finally{foreach(var value in objects)Marshal.FinalReleaseComObject(value);if(operation is not null)Marshal.FinalReleaseComObject(operation);GC.KeepAlive(sinks);}
            if(failure is OperationCanceledException canceled)done.SetCanceled(canceled.CancellationToken);else if(failure is not null)done.SetException(failure);else done.SetResult(batch!);
        }){IsBackground=true,Name="FolderLens Windows Shell operation"};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
    private static void Validate(ShellFileRequest request)
    {
        string source=PathRules.ValidateSource(request.Source);File.GetAttributes(source);
        if(request.Identity is not null&&FileAllocation.InspectMetadata(source).PhysicalIdentity!=request.Identity)throw new IOException("文件身份已变化，请刷新后重试。");
        if(request.Stamp is {} stamp){var f=new FileInfo(source);if(new SourceFileStamp(f.Length,f.LastWriteTimeUtc.Ticks)!=stamp)throw new IOException("文件已变化，请刷新后重试。");}
        if(request.Action==ShellFileAction.Rename)FileOperations.RenameTarget(source,request.NewName??"");
    }
    private static bool SameDirectory(string a,string b)
    {var first=FileAllocation.InspectMetadata(a);return first.PhysicalIdentity is not null&&first.PhysicalIdentity==FileAllocation.InspectMetadata(b).PhysicalIdentity;}
    private static bool SameRenameEntry(ShellFileRequest request,string target)
    {
        if(request.Action!=ShellFileAction.Rename||!string.Equals(Path.GetFileName(request.Source),Path.GetFileName(target),StringComparison.OrdinalIgnoreCase))return false;
        var a=FileAllocation.InspectMetadata(request.Source,true);var b=FileAllocation.InspectMetadata(target,true);
        return a.PhysicalIdentity is not null&&a.PhysicalIdentity==b.PhysicalIdentity&&a.ResolvedLocation is not null&&a.ResolvedLocation==b.ResolvedLocation;
    }
    internal static ShellItemOutcome VerifyOutcome(ShellFileRequest request,string? destination,int hr,string? parentIdentity)
    {
        // Shell reports DONT_PROCESS_CHILDREN for successful leaf renames, not S_OK.
        // Skip/retry/pending statuses are deliberately not classified as success.
        if(hr is not (0 or 0x00270008 or 0x00270006 or 0x0027000A or 0x0027000C or 0x0027000E))return ShellItemOutcome.NotCompleted;
        try
        {
            if(request.Action!=ShellFileAction.Recycle)
            {
                if(destination is null)return ShellItemOutcome.Indeterminate;
                File.GetAttributes(destination);
                if(request.Stamp is {} stamp){var f=new FileInfo(destination);if(new SourceFileStamp(f.Length,f.LastWriteTimeUtc.Ticks)!=stamp)return ShellItemOutcome.Indeterminate;}
            }
            if(request.Action==ShellFileAction.Copy)return ShellItemOutcome.Completed;
            if(request.Action==ShellFileAction.Rename&&destination is not null&&SameRenameEntry(request,destination))
            {
                var result=FileAllocation.InspectMetadata(destination,true);
                return Path.GetFileName(result.ResolvedLocation)==request.NewName?ShellItemOutcome.Completed:ShellItemOutcome.SourceRetained;
            }
            try{File.GetAttributes(request.Source);return ShellItemOutcome.SourceRetained;}
            catch(FileNotFoundException)
            {
                // File.Exists also returns false for access errors: only a genuine missing leaf
                // beneath the same accessible parent is enough to retire a saved location.
                return parentIdentity is not null&&FileAllocation.InspectMetadata(Path.GetDirectoryName(request.Source)!).PhysicalIdentity==parentIdentity?ShellItemOutcome.Completed:ShellItemOutcome.Indeterminate;
            }
        }
        catch(IOException){return ShellItemOutcome.Indeterminate;}
        catch(UnauthorizedAccessException){return ShellItemOutcome.Indeterminate;}
    }
    private static IShellItem Item(string path){var id=typeof(IShellItem).GUID;Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(Path.GetFullPath(path),0,ref id,out var item));return item;}
    private static string? PathOf(IShellItem? item)
    {if(item is null)return null;try{item.GetDisplayName(0x80058000,out nint name);try{return Marshal.PtrToStringUni(name);}finally{Marshal.FreeCoTaskMem(name);}}catch(COMException){return null;}}

    [ComVisible(true),ClassInterface(ClassInterfaceType.None)]
    private sealed class Sink(ShellFileRequest request,CancellationToken token,bool control=false):IFileOperationProgressSink
    {
        public bool Control {get;}=control;
        public ShellFileRequest Request {get;}=request;
        private readonly string? parentIdentity=FileAllocation.InspectMetadata(Path.GetDirectoryName(request.Source)!).PhysicalIdentity;
        public ShellFileResult? Result {get;private set;}
        private int Before(){if(token.IsCancellationRequested)return unchecked((int)0x800704C7);if(Control)return 0;try{Validate(Request);return 0;}catch(Exception ex){return Marshal.GetHRForException(ex);}}
        private int After(int hr,IShellItem? created){if(Control)return 0;string? path=PathOf(created);Result=new(Request,VerifyOutcome(Request,path,hr,parentIdentity),hr,path);return 0;}
        public int StartOperations()=>0;public int FinishOperations(int hr)=>0;
        public int PreRenameItem(uint flags,IShellItem item,string name)=>Before();public int PostRenameItem(uint flags,IShellItem item,string name,int hr,IShellItem? created)=>After(hr,created);
        public int PreMoveItem(uint flags,IShellItem item,IShellItem folder,string? name)=>Before();public int PostMoveItem(uint flags,IShellItem item,IShellItem folder,string? name,int hr,IShellItem? created)=>After(hr,created);
        public int PreCopyItem(uint flags,IShellItem item,IShellItem folder,string? name)=>Before();public int PostCopyItem(uint flags,IShellItem item,IShellItem folder,string? name,int hr,IShellItem? created)=>After(hr,created);
        public int PreDeleteItem(uint flags,IShellItem item)=>Before();public int PostDeleteItem(uint flags,IShellItem item,int hr,IShellItem? created)=>After(hr,created);
        public int PreNewItem(uint flags,IShellItem folder,string name)=>0;public int PostNewItem(uint flags,IShellItem folder,string name,string? template,uint attributes,int hr,IShellItem? created)=>0;
        public int UpdateProgress(uint total,uint done)=>token.IsCancellationRequested?unchecked((int)0x800704C7):0;
        public int ResetTimer()=>0;public int PauseTimer()=>0;public int ResumeTimer()=>0;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)]private static extern int SHCreateItemFromParsingName(string path,nint context,ref Guid iid,out IShellItem item);
    [ComImport,Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {void BindToHandler(nint context,ref Guid handler,ref Guid iid,out nint result);void GetParent(out IShellItem parent);void GetDisplayName(uint kind,out nint name);void GetAttributes(uint mask,out uint attributes);void Compare(IShellItem other,uint hint,out int order);}
    [ComImport,Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        void Advise(IFileOperationProgressSink sink,out uint cookie);void Unadvise(uint cookie);void SetOperationFlags(uint flags);void SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)]string message);void SetProgressDialog(nint dialog);void SetProperties(nint properties);void SetOwnerWindow(nint owner);
        void ApplyPropertiesToItem(IShellItem item);void ApplyPropertiesToItems([MarshalAs(UnmanagedType.IUnknown)]object items);
        void RenameItem(IShellItem item,[MarshalAs(UnmanagedType.LPWStr)]string name,IFileOperationProgressSink sink);void RenameItems([MarshalAs(UnmanagedType.IUnknown)]object items,[MarshalAs(UnmanagedType.LPWStr)]string name);
        void MoveItem(IShellItem item,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string? name,IFileOperationProgressSink sink);void MoveItems([MarshalAs(UnmanagedType.IUnknown)]object items,IShellItem folder);
        void CopyItem(IShellItem item,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string? name,IFileOperationProgressSink sink);void CopyItems([MarshalAs(UnmanagedType.IUnknown)]object items,IShellItem folder);
        void DeleteItem(IShellItem item,IFileOperationProgressSink sink);void DeleteItems([MarshalAs(UnmanagedType.IUnknown)]object items);
        void NewItem(IShellItem folder,uint attributes,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string? template,IFileOperationProgressSink sink);
        [PreserveSig]int PerformOperations();void GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)]out bool aborted);
    }
    [ComVisible(true),Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig]int StartOperations();[PreserveSig]int FinishOperations(int hr);
        [PreserveSig]int PreRenameItem(uint flags,IShellItem item,[MarshalAs(UnmanagedType.LPWStr)]string name);[PreserveSig]int PostRenameItem(uint flags,IShellItem item,[MarshalAs(UnmanagedType.LPWStr)]string name,int hr,IShellItem? created);
        [PreserveSig]int PreMoveItem(uint flags,IShellItem item,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string? name);[PreserveSig]int PostMoveItem(uint flags,IShellItem item,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string? name,int hr,IShellItem? created);
        [PreserveSig]int PreCopyItem(uint flags,IShellItem item,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string? name);[PreserveSig]int PostCopyItem(uint flags,IShellItem item,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string? name,int hr,IShellItem? created);
        [PreserveSig]int PreDeleteItem(uint flags,IShellItem item);[PreserveSig]int PostDeleteItem(uint flags,IShellItem item,int hr,IShellItem? created);
        [PreserveSig]int PreNewItem(uint flags,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string name);[PreserveSig]int PostNewItem(uint flags,IShellItem folder,[MarshalAs(UnmanagedType.LPWStr)]string name,[MarshalAs(UnmanagedType.LPWStr)]string? template,uint attributes,int hr,IShellItem? created);
        [PreserveSig]int UpdateProgress(uint total,uint done);[PreserveSig]int ResetTimer();[PreserveSig]int PauseTimer();[PreserveSig]int ResumeTimer();
    }
}
