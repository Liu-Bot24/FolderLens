using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyLargeText(string source,Dictionary<string,object> report)
    {
        if(new DriveInfo(Path.GetPathRoot(source)!).AvailableFreeSpace<8L*1024*1024*1024)
            throw new IOException("Large-text verification needs 8 GiB of free fixture space.");
        const string phrase="FolderLens 内容边界测试 0123456789\r\n";
        byte[] line=Encoding.UTF8.GetBytes(phrase),block=new byte[1024*1024];
        int filled=0;while(filled+line.Length<=block.Length){line.CopyTo(block,filled);filled+=line.Length;}
        Array.Fill(block,(byte)' ',filled,block.Length-filled);
        var fixtures=new List<(string Name,long Length,string Hash,DateTime Modified)>();
        foreach(int gib in new[]{1,5})
        {
            string name=$"native-{gib}GiB.txt",path=Path.Combine(source,name);long length=(long)gib*1024*1024*1024;
            string hash=await Task.Run(()=>
            {
                using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,block.Length,FileOptions.SequentialScan);
                for(long written=0;written<length;written+=block.Length)
                {lifetime.Token.ThrowIfCancellationRequested();file.Write(block);sha.AppendData(block);}
                file.Flush(true);return Convert.ToHexString(sha.GetHashAndReset());
            },lifetime.Token);
            fixtures.Add((name,length,hash,File.GetLastWriteTimeUtc(path)));
        }
        suppressFilters=true;SelectTag(Category,"text");suppressFilters=false;
        await OpenRoot(source);await RefreshQuery();
        var cases=new List<object>();report["cases"]=cases;
        foreach(var fixture in fixtures)
        {
            long ordinal=await catalog!.FindOrdinal(resultHandle!.Id,fixture.Name)??throw new InvalidOperationException("Large text fixture missing.");
            var first=Stopwatch.StartNew();
            if(!await SelectBrowserOrdinal(results!,checked((int)ordinal),lifetime.Token)||previewReadySelection!=selection||previewFailure is not null)
                throw new InvalidOperationException("Large text selection failed: "+QualityLabel.Text);
            await SetImmersive(true);Shell.UpdateLayout();
            if(TextScroll.Visibility!=Visibility.Visible||displayedText?.Length!=fixture.Length||!TextContent.Text.Contains("内容边界测试"))
                throw new InvalidOperationException("Large text did not reach the native reader.");
            double firstWindowAndLayoutMs=first.Elapsed.TotalMilliseconds;
            var measurements=new List<object>();
            for(int sample=0;sample<100;sample++)
            {
                long requested=sample%2==0?0:Math.Min(fixture.Length-1024,4L*1024*1024*1024+7);
                var clock=Stopwatch.StartNew();await LoadText(requested,selection,selectionStop.Token);Shell.UpdateLayout();
                if(displayedText is not {} page||page.Length!=fixture.Length||page.Start>requested||page.Start<requested-3||page.Next-page.Start>64*1024||TextContent.Text.Length>64*1024||!TextContent.Text.Contains("内容边界测试"))
                    throw new InvalidOperationException("Large text window was stale, unbounded, or incorrectly decoded.");
                measurements.Add(new{sample,requested,actual=page.Start,next=page.Next,characters=TextContent.Text.Length,windowAndLayoutMs=clock.Elapsed.TotalMilliseconds});
            }
            await LoadText(0,selection,selectionStop.Token);Shell.UpdateLayout();long next=textNext;
            TextScroll.ChangeView(null,TextScroll.ScrollableHeight,null,true);await Task.Delay(50,lifetime.Token);
            await PageReader(1);Shell.UpdateLayout();
            if(textStart!=next||TextScroll.VerticalOffset>1)throw new InvalidOperationException("Large text page did not advance to the next bounded block.");
            await PageReader(-1);Shell.UpdateLayout();
            if(textStart!=0||!TextContent.Text.StartsWith("FolderLens"))throw new InvalidOperationException("Large text previous block did not restore the beginning.");
            string actualHash=await Task.Run(()=>{using var file=File.OpenRead(Path.Combine(source,fixture.Name));return Convert.ToHexString(SHA256.HashData(file));},lifetime.Token);
            if(actualHash!=fixture.Hash||File.GetLastWriteTimeUtc(Path.Combine(source,fixture.Name))!=fixture.Modified)
                throw new InvalidOperationException("Large text source changed during reading.");
            cases.Add(new{fixture.Name,fixture.Length,sha256=actualHash,firstWindowAndLayoutMs,measurements,resources=WorkerResources.Shared.Snapshot});
            await ReturnToBrowser();
        }
        report["scope"]="Real non-sparse UTF-8 files, real Content.Worker and WinUI text control, offscreen without activation. Warm OS cache after generation. Timings end after UpdateLayout, not physical display Present; no physical input, frame-time or complete text-memory-budget claim.";
        report["status"]="PASS";
    }
}
