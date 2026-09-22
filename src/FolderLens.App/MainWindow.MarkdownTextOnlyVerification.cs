using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyMarkdownTextOnlyLifecycle(string source,Dictionary<string,object> report)
    {
        await File.WriteAllTextAsync(Path.Combine(source,"lifecycle.md"),"# Lifecycle\n\nA document without media.\n");
        await File.WriteAllTextAsync(Path.Combine(source,"lifecycle.txt"),"The original text selection.\n");
        suppressFilters=true;try{SelectTag(Category,"text");}finally{suppressFilters=false;}
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;await RefreshQuery();
        async Task<FileRow> Row(string path)
        {
            long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,path,lifetime.Token)??throw new InvalidOperationException("Missing fixture: "+path);
            var row=(FileRow)results![checked((int)ordinal)]!;await results.EnsureLoaded(row,lifetime.Token);return row;
        }
        var document=await Row("lifecycle.md");var plain=await Row("lifecycle.txt");
        var iterations=new List<object>();report["documentLoads"]=iterations;
        for(int i=0;i<3;i++)
        {
            var watch=System.Diagnostics.Stopwatch.StartNew();
            await SelectPreview(document);
            bool visible=MarkdownHost.Visibility==Visibility.Visible&&markdown?.CoreWebView2 is not null;
            iterations.Add(new{iteration=i,visible,elapsedMs=watch.Elapsed.TotalMilliseconds,quality=QualityLabel.Text});
            if(!visible)throw new InvalidOperationException("Text-only Markdown could not reopen at iteration "+i+": "+QualityLabel.Text);
            if(await markdown!.CoreWebView2.ExecuteScriptAsync("document.body.innerText.includes('Lifecycle')")!="true")throw new InvalidOperationException("The current text-only document did not display.");
            await SelectPreview(plain);
            if(TextScroll.Visibility!=Visibility.Visible||MarkdownHost.Visibility!=Visibility.Collapsed)throw new InvalidOperationException("The original text did not replace the rendered document.");
            await DisposeMarkdownView();
            // Reopen after native shutdown has had time to begin, rather than
            // accidentally reusing a browser that has not started exiting yet.
            if(i<2)await Task.Delay(200);
        }
        report["status"]="PASS";
    }
}
