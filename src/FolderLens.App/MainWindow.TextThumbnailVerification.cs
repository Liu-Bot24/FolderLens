using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace FolderLens.App;
public sealed partial class MainWindow
{
    private async Task VerifyTextThumbnails(string source,Dictionary<string,object> report)
    {
        string folder=Path.Combine(source,"text-cards");Directory.CreateDirectory(folder);
        string content="文字摘录示例\n\n  \n在文件列表中，直接查看文档开头。\n缩略图放大后可以显示更多文字；缩小时保留清楚的排版。\n这是一份包含中文、English 和数字 123 的说明。\n";
        await File.WriteAllTextAsync(Path.Combine(folder,"notes.txt"),string.Concat(Enumerable.Repeat(content,20000)));
        await File.WriteAllTextAsync(Path.Combine(folder,"readme.md"),"# 项目使用说明\n\n"+content+"\n![不加载网络图片](https://example.invalid/image.png)\n");
        var reads=new List<int>();verifyTextExcerptRead=(_,bytes)=>reads.Add(bytes);
        try
        {
            ThumbnailSize.Value=100;suppressFilters=true;SelectTag(Category,"all");suppressFilters=false;
            await OpenRoot(folder);if(scanTask is not null)await scanTask;await RefreshQuery();
            await WaitUntil(()=>visible.Count(r=>r.IsTextCard)==2&&thumbnailRequests.Count==0,TimeSpan.FromSeconds(15));
            await Task.Delay(300);if(reads.Count!=0)throw new InvalidOperationException("最小卡片读取了文本正文。");
            async Task Capture(string name)
            {
                FilesGrid.UpdateLayout();var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync((UIElement)FilesGrid.ContainerFromItem(visible.First(r=>r.Name=="notes.txt")));
                using var file=File.Create(Path.Combine(dataDirectory,name+".png"));using var stream=file.AsRandomAccessStream();
                var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,(await bitmap.GetPixelsAsync()).ToArray());await encoder.FlushAsync();
            }
            await Capture("text-100");
            foreach(int size in new[]{160,240,300})
            {
                ThumbnailSize.Value=size;UpdateGridPresentation();
                await WaitUntil(()=>visible.Count(r=>r.IsTextCard&&!r.NeedsTextExcerpt&&r.TextExcerptVisibility==Visibility.Visible)==2&&thumbnailRequests.Count==0,TimeSpan.FromSeconds(15));
                foreach(var row in visible.Where(r=>r.IsTextCard))
                    if(row.TextExcerpt.Length>row.TextLayout.Characters||row.TextExcerpt.Length==0||row.FileIconVisibility!=Visibility.Collapsed||row.ThumbnailError.Length>0)throw new InvalidOperationException("文字摘录或图标状态错误。");
                if(ThumbnailSize.Value!=size)throw new InvalidOperationException("缩略图滑块未达到请求尺寸。");
                if(visible.Where(r=>r.IsTextCard).Any(r=>r.TextExcerpt.Split('\n').Any(string.IsNullOrWhiteSpace)))throw new InvalidOperationException("文字缩略图仍有空行。");
                report["size"+size]=visible.Where(r=>r.IsTextCard).Select(r=>new{r.CardWidth,r.TextExcerptLines,characters=r.TextExcerpt.Length,budget=r.TextLayout.ReadBytes}).ToArray();
                await Capture("text-"+size);
            }
            int warm=reads.Count;
            foreach(int size in new[]{160,100,300}){ThumbnailSize.Value=size;UpdateGridPresentation();await Task.Delay(250);}
            if(reads.Count!=warm)throw new InvalidOperationException("缩小再放大重复读取已有摘录。");
            var original=visible.First(r=>r.IsTextCard);var changed=new FileRow(0);changed.Fill(original.Item!);changed.SetTextExcerpt("old text",1024,true);
            changed.Fill(original.Item! with{Version=original.Item!.Version+1});
            if(changed.TextExcerptVisibility==Visibility.Visible||changed.TextExcerpt.Length!=0)throw new InvalidOperationException("新版本沿用了旧摘录。");
            var failed=new FileRow(0);failed.Fill(original.Item!);failed.SetPresentation(240,false);failed.SetTextExcerpt("cached",1024,true);failed.FailThumbnail(new IOException("FileChanged"));
            if(failed.TextExcerptVisibility!=Visibility.Collapsed||failed.ThumbnailErrorVisibility!=Visibility.Visible)throw new InvalidOperationException("摘录掩盖了读取失败。");
            var old=new FileRow(0);old.Fill(original.Item!);old.SetTextExcerpt("evict",1024,true);RememberTextExcerpt(old);
            for(int n=0;n<150;n++){var cached=new FileRow(n);cached.Fill(original.Item!);cached.SetTextExcerpt("cached",1024,true);RememberTextExcerpt(cached);}
            if(old.HasTextExcerpt||textExcerptRows.Count(r=>!visible.Contains(r))>128||!original.HasTextExcerpt)throw new InvalidOperationException("摘录缓存未限制离屏保留，或误删可见卡片。");
            report["boundedOffscreenCache"]=true;
            if(reads.Any(bytes=>bytes>2048))throw new InvalidOperationException("摘录超出读取预算。");
            report["requestedByteBudgets"]=reads;report["smallCardsDoNotReadText"]=true;report["resizeReusesExcerpt"]=true;report["versionAndErrorIsolation"]=true;report["status"]="PASS";
        }
        finally{verifyTextExcerptRead=null;}
    }
}
