using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private static IEnumerable<FrameworkElement> InputDescendants(DependencyObject parent)
    {
        if(parent is FrameworkElement element)yield return element;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            foreach(var child in InputDescendants(VisualTreeHelper.GetChild(parent,i)))yield return child;
    }
    private async Task VerifyInputAlignment(Dictionary<string,object> report)
    {
        var observations=new List<object>();var failures=new List<string>();
        report["measurements"]=observations;report["failures"]=failures;
        suppressFilters=true;
        async Task Measure(TextBox box,string label,bool placeholder=false,string state="Normal")
        {
            string saved=box.Text,hint=box.PlaceholderText;bool enabled=box.IsEnabled;
            box.PlaceholderText="发大水 Ag09";box.Text=placeholder?"":"发大水 Ag09";
            try
            {
                Shell.UpdateLayout();await Task.Delay(25);
                box.IsEnabled=state!="Disabled";VisualStateManager.GoToState(box,state,false);await Task.Delay(25);
                var border=InputDescendants(box).First(e=>e.Name=="BorderElement");
                var bounds=border.TransformToVisual(box).TransformBounds(new(0,0,border.ActualWidth,border.ActualHeight));
                var bitmap=new RenderTargetBitmap();await bitmap.RenderAsync(box);
                byte[] pixels=(await bitmap.GetPixelsAsync()).ToArray();int w=bitmap.PixelWidth,h=bitmap.PixelHeight;
                double scale=h/box.ActualHeight;
                int top=(int)((bounds.Y+5)*scale),bottom=Math.Min(h,(int)((bounds.Bottom-5)*scale));
                int paper=4*((int)((bounds.Y+bounds.Height/2)*scale)*w+Math.Max(0,w-(int)(45*scale)));
                int min=h,max=-1;
                for(int y=top;y<bottom;y++)for(int x=(int)(12*scale);x<Math.Min(w-(int)(50*scale),(int)(130*scale));x++)
                {
                    int at=4*(y*w+x);
                    // Disabled fields may have a translucent background. Black
                    // glyphs change alpha without changing premultiplied RGB.
                    int contrast=0;for(int channel=0;channel<4;channel++)contrast=Math.Max(contrast,Math.Abs(pixels[at+channel]-pixels[paper+channel]));
                    if(contrast<40)continue;
                    min=Math.Min(min,y);max=Math.Max(max,y);
                }
                double offset=((min+max+1)/2d)/scale-(bounds.Y+bounds.Height/2);
                observations.Add(new{label,placeholder,box.FontSize,box.ActualHeight,fieldHeight=bounds.Height,glyphTop=min,glyphBottom=max,offset});
                if(max<min||Math.Abs(offset)>2.5)failures.Add($"{label}: 文字中心偏移 {offset:N2} DIP");
                using var file=File.Create(Path.Combine(dataDirectory,$"input-{label}.png"));using var stream=file.AsRandomAccessStream();
                var encoder=await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId,stream);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied,(uint)w,(uint)h,96,96,pixels);await encoder.FlushAsync();
            }
            finally{box.IsEnabled=enabled;box.Text=saved;box.PlaceholderText=hint;}
        }
        try
        {
            foreach(int width in new[]{360,520,1000})
            {
                BrowserPane.Width=width;UpdateBrowserToolbar();Shell.UpdateLayout();
                foreach(var box in new[]{Search,RootPath})
                {
                    foreach(string state in new[]{"Normal","Focused","Disabled"})
                    {
                        await Measure(box,$"{box.Name}-{width}-{state}",state:state);
                    }
                    VisualStateManager.GoToState(box,"Normal",false);
                    await Measure(box,$"{box.Name}-{width}-placeholder",true);
                }
            }
            // Native dialog templates have headers and different font metrics.
            // Keep their defaults and verify them independently of toolbar styles.
            var samples=new StackPanel{Spacing=8,Width=360};
            samples.Children.Add(new TextBox{Header="收藏夹名称",MaxLength=100,PlaceholderText="输入名称"});
            samples.Children.Add(new TextBox{Header="新文件名（包含扩展名）",Text="example.txt"});
            samples.Children.Add(new TextBox{Header="数值范围",PlaceholderText="不限"});
            samples.Children.Add(new NumberBox{Header="播放间隔",Value=5,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact});
            var select=new ComboBox{Header="匹配方式",HorizontalAlignment=HorizontalAlignment.Stretch};select.Items.Add("完全相同");select.SelectedIndex=0;samples.Children.Add(select);
            var dialog=new ContentDialog{XamlRoot=Shell.XamlRoot,Content=samples,CloseButtonText="关闭"};var shown=dialog.ShowAsync();
            try
            {
                await WaitUntil(()=>samples.IsLoaded,TimeSpan.FromSeconds(3));dialog.UpdateLayout();
                var boxes=InputDescendants(samples).OfType<TextBox>().Where(box=>box.Visibility==Visibility.Visible&&box.ActualWidth>0&&box.ActualHeight>0).ToArray();
                if(boxes.Length!=4)throw new InvalidOperationException("未覆盖三个普通字段及 NumberBox 内部输入框。");
                for(int i=0;i<boxes.Length;i++)
                {
                    await Measure(boxes[i],$"dialog-{i}");
                    await Measure(boxes[i],$"dialog-{i}-placeholder",true);
                }
                report["dialogTextInputs"]=boxes.Length;
            }
            finally{dialog.Hide();await shown;}
            if(TextContent.VerticalAlignment!=VerticalAlignment.Stretch||!TextContent.AcceptsReturn)failures.Add("文档正文必须保留撑满预览区的多行布局。");
        }
        finally{suppressFilters=false;}
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        report["status"]="PASS";
    }

    private async Task VerifyCategoryPending(string source,Dictionary<string,object> report)
    {
        await File.WriteAllTextAsync(Path.Combine(source,"index.md"),"# Markdown fixture");
        suppressFilters=true;SelectTag(Category,"text");suppressFilters=false;
        folderGrouping=new(true);UpdateGroupingButton();
        await OpenRoot(source);if(scanTask is not null)await scanTask;if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await WaitUntil(()=>results?.Count==1&&!queryBusy,TimeSpan.FromSeconds(10));
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        verifyCandidateBarrier=async token=>{entered.TrySetResult();await release.Task.WaitAsync(token);};
        try
        {
            SelectTag(Category,"media");await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Shell.UpdateLayout();
            bool staleVisible=ActiveBrowser.Items.Count>0||resultHandle is not null||results is not null;
            report["staleTextVisibleWhileMediaPending"]=staleVisible;
            release.TrySetResult();await queryCompletion;
            if(staleVisible)throw new InvalidOperationException("图片＋视频查询等待期间仍展示旧文本列表。");
            if(lastAppliedFilter?.Kinds.SequenceEqual(new[]{"image","video"})!=true)throw new InvalidOperationException("图片＋视频条件未发布。");
            var items=await catalog!.ReadPage(resultHandle!.Id,0,100);
            if(items.Any(item=>item.Kind is not ("image" or "video")))throw new InvalidOperationException("媒体查询中混入非媒体文件。");
            report["mediaFiles"]=items.Count;
        }
        finally{release.TrySetResult();verifyCandidateBarrier=null;}
        // A failed same-filter refresh keeps the valid view; a different filter
        // failure must leave an honest error state with no stale actionable rows.
        var retained=resultHandle;
        verifyCandidateBarrier=_=>throw new IOException("Verification query failure");
        try
        {
            await RefreshQuery();
            if(!ReferenceEquals(retained,resultHandle)||ActiveBrowser.Items.Count==0)throw new InvalidOperationException("同条件查询失败丢失了有效列表。");
            SelectTag(Category,"text");await queryCompletion;
            if(resultHandle is not null||ActiveBrowser.Items.Count!=0||browserEmptyError is null||BrowserEmptyState.Visibility!=Visibility.Visible)
                throw new InvalidOperationException("切换分类失败没有清除旧结果并显示错误。");
            report["failureStates"]=true;
        }
        finally{verifyCandidateBarrier=null;}
        await RefreshQuery();
        if(resultHandle?.Count!=1||(await catalog!.ReadPage(resultHandle.Id,0,10)).Any(item=>item.Kind!="markdown"))throw new InvalidOperationException("失败后无法恢复文本分类。");
        for(int i=0;i<12;i++)SelectTag(Category,i%2==0?"media":"text");
        await queryCompletion;
        if(resultHandle?.Count!=1||lastAppliedFilter?.Kinds.Contains("markdown")!=true)throw new InvalidOperationException("连续分类切换被旧请求覆盖。");
        report["rapidCategoryChanges"]=12;report["status"]="PASS";
    }
}
