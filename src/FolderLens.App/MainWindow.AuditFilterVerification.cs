using FolderLens.Core;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyAuditFilters(string source,Dictionary<string,object> report)
    {
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;
        monitor?.Dispose();monitor=null;
        var accepted=resultHandle;long query=queryRequest,navigation=rootChangeVersion;
        var policy=CurrentFilter();
        suppressFilters=true;
        try
        {
            MinSize.Value=2;MaxSize.Value=1;
            await ApplyBrowserFilters();
            if(!ReferenceEquals(resultHandle,accepted)||queryRequest!=query)throw new InvalidOperationException("非法区间启动查询或替换了结果。");
            await OpenRoot(Path.Combine(source,"A"));
            if(rootChangeVersion!=navigation||queryRequest!=query||!ReferenceEquals(resultHandle,accepted))throw new InvalidOperationException("非法草稿导航改变了已接受视图。");
            MinSize.Value=double.MaxValue;MaxSize.Value=double.NaN;
            await ApplyBrowserFilters();
            if(queryRequest!=query||!ReferenceEquals(resultHandle,accepted))throw new InvalidOperationException("溢出输入启动了查询。");
            ApplySavedFilter(policy with{Ranges=new(){["width"]=new(1920,4096),["height"]=new(1080,null)}});
            MinWidth.Value=double.NaN;MinHeight.Value=double.NaN;
            var cleared=CurrentFilter();
            if(cleared.Ranges["width"]!=new IntRange(null,4096)||cleared.Ranges.ContainsKey("height"))throw new InvalidOperationException("清空宽高下限未清除保存视图中的旧下限。");
            ApplySavedFilter(policy);await ApplyBrowserFilters();
            if(queryRequest<=query||resultHandle is null)throw new InvalidOperationException("纠正输入后未恢复查询。");
            folderGrouping=new(true);await OpenRoot(source);
            if(!folderGrouping.Enabled||browserGroups is null)throw new InvalidOperationException("同目录导航丢失了有效分组条件。");
            report["validDraftGroupingSurvivesNavigation"]=true;
            report["invalidDraftPreservesAcceptedView"]=true;report["clearedMinimaPreserveMaxima"]=true;
            report["status"]="PASS";
        }
        finally{suppressFilters=false;}
    }
}
