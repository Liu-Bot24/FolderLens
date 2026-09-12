using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyRangePublicationCost(Dictionary<string,object> report)
    {
        const int initial=1000,added=20000;
        var samples=new List<object>();report["samples"]=samples;
        bool excessAllocation=false;
        foreach(string mode in new[]{"unbound","view","grid"})
        {
            FilesGrid.ItemsSource=null;FilesList.ItemsSource=null;
            // Isolate range publication from file-row creation and database/decoder work.
            long rowAllocationStart=GC.GetAllocatedBytesForCurrentThread();
            var rows=Enumerable.Range(0,initial+added).Select(i=>new FileRow(i)).ToArray();
            long rowAllocation=GC.GetAllocatedBytesForCurrentThread()-rowAllocationStart;
            if(rowAllocation>rows.Length*220L)excessAllocation=true;
            int Locate(object? value)=>value is FileRow row&&row.Ordinal>=0&&row.Ordinal<rows.Length&&ReferenceEquals(rows[row.Ordinal],row)?(int)row.Ordinal:-1;
            var items=new VirtualRangeCollection<FileRow>(initial,i=>rows[i],Locate);
            CollectionViewSource? cvs=null;
            if(mode!="unbound")
            {
                cvs=new(){IsSourceGrouped=true,ItemsPath=new PropertyPath(nameof(VerificationFileGroup.Items)),Source=new[]{new VerificationFileGroup(items)}};
                var view=cvs.View;if(mode=="grid")FilesGrid.ItemsSource=view;
            }
            Shell.UpdateLayout();await Task.Delay(30);
            var before=FilesGrid.ContainerFromIndex(0);
            long allocated=GC.GetAllocatedBytesForCurrentThread();var timer=System.Diagnostics.Stopwatch.StartNew();
            items.UpdateRanges([new(initial,0,added)],i=>rows[i],Locate);
            timer.Stop();long bytes=GC.GetAllocatedBytesForCurrentThread()-allocated;
            if(mode=="unbound"&&bytes>added*180L)excessAllocation=true;
            Shell.UpdateLayout();
            bool containerRetained=mode!="grid"||before is not null&&ReferenceEquals(before,FilesGrid.ContainerFromIndex(0));
            samples.Add(new{mode,milliseconds=timer.Elapsed.TotalMilliseconds,allocatedBytes=bytes,bytesPerItem=bytes/(double)added,rowAllocationBytes=rowAllocation,rowBytesPerItem=rowAllocation/(double)rows.Length,containerRetained,viewCount=cvs?.View.Count});
            if(items.Count!=rows.Length||cvs is not null&&cvs.View.Count!=rows.Length||!containerRetained)throw new InvalidOperationException("通知后真实视图数量或原可见容器不一致。");
            if(!ReferenceEquals(items[0],rows[0])||!ReferenceEquals(items[items.Count-1],rows[^1]))throw new InvalidOperationException("更新后索引内容不一致。");
            FilesGrid.ItemsSource=null;if(cvs is not null)cvs.Source=null;
        }
        if(excessAllocation)throw new InvalidOperationException("纯集合发布超过180字节/项或文件行创建超过220字节/项预算。");
        using var failedSource=new VirtualResults(1,(_,_)=>throw new IOException("Fixture page failure"));
        var failedRow=(FileRow)failedSource[0]!;bool propagated=false;
        try{await failedSource.EnsureLoaded(failedRow,CancellationToken.None);}catch(IOException){propagated=true;}
        if(!propagated||failedRow.Name!="加载失败"||failedRow.Detail!="Fixture page failure")throw new InvalidOperationException("移除未使用任务后加载失败没有保留错误。");
        failedSource.Dispose();bool canceled=false;
        try{await failedSource.EnsureLoaded((FileRow)failedSource[0]!,CancellationToken.None);}catch(OperationCanceledException){canceled=true;}
        if(!canceled)throw new InvalidOperationException("退役源仍允许读取占位行。");
        report["failureAndRetirementPreserved"]=true;
        report["status"]="PASS";
    }
}
