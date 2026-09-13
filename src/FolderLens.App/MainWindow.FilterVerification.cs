using System.Runtime.InteropServices.WindowsRuntime;
using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyFilterEditor(Dictionary<string, object> report)
    {
        var checks = new List<string>(); report["checks"] = checks;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        var source = new FilterSpec { RootId = "filter-fixture", Dates = [new("modified", "utc", "2023-01-02T03:04:05Z", "2023-02-03T04:05:06Z"), new("captured", "captureWall", "2023-03-04T12:13:14", "2023-04-05T13:14:15")], Ranges = new() { ["logicalBytes"] = new(20, 500) } };
        var editor = new AdvancedFilterEditor(source);
        Check(editor.Dates["modified"].From.Date is not null && editor.Dates["captured"].To.Date is not null, "已有日期回填到原生控件");
        var unchanged = editor.Read([]);
        Check(unchanged.Dates.SequenceEqual(source.Dates) && unchanged.Ranges["logicalBytes"] == source.Ranges["logicalBytes"], "未编辑日期保留精确时间、时钟和大小条件");
        Check(!editor.Ranges.ContainsKey("durationMs") && !editor.View.Children.OfType<Expander>().Any(x => (string)x.Header == "播放信息"), "图片不提供时长和编码条件");
        Check(new AdvancedFilterEditor(source with { Kinds = ["video"] }).Ranges.ContainsKey("durationMs"), "视频提供时长条件");
        Check(!new AdvancedFilterEditor(source with { Kinds = ["audio"], Dates = [] }).Ranges.ContainsKey("width"), "音频不提供画面尺寸条件");
        var documents=new AdvancedFilterEditor(source with{Kinds=[],Extensions=["pdf"],Dates=[]});
        Check(!documents.Ranges.ContainsKey("width")&&!documents.Ranges.ContainsKey("durationMs")&&!documents.Dates.ContainsKey("captured"),"PDF 不提供图片和媒体专用条件");
        var presetPeer=new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(documents.DatePresetButtons["modified:最近 7 天"]);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)presetPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        var recent=documents.Read([]).Dates.Single();
        Check(DateTimeOffset.Parse(recent.StartInclusive).LocalDateTime.Date==DateTime.Today.AddDays(-6)&&DateTimeOffset.Parse(recent.EndExclusive).LocalDateTime.Date==DateTime.Today.AddDays(1),"最近 7 天按本地日历包含今天且排除明天");
        var inherited = new AdvancedFilterEditor(source with { Ranges = new() { ["durationMs"] = new(100, 1000) } });
        Check(inherited.Read([]).Ranges.ContainsKey("durationMs"), "跨类型已有条件保持可编辑并保留");
        inherited.Ranges["durationMs"].Min.Text = ""; inherited.Ranges["durationMs"].Max.Text = "";
        Check(!inherited.Read([]).Ranges.ContainsKey("durationMs"), "跨类型条件可以显式清除");
        var capture = editor.Dates["captured"];
        capture.From.Date = new DateTimeOffset(2024, 5, 6, 0, 0, 0, TimeSpan.Zero); capture.To.Date = new DateTimeOffset(2024, 5, 7, 0, 0, 0, TimeSpan.Zero);
        var changed = editor.Read([]).Dates.Single(d => d.Field == "captured");
        Check(changed.StartInclusive == "2024-05-06T00:00:00" && changed.EndExclusive == "2024-05-08T00:00:00", "拍摄日期生成有效墙钟且结束日期包含全天");
        capture.To.Date = null; bool rejected = false;
        try { editor.Read([]); } catch (ArgumentException) { rejected = true; }
        Check(rejected && source.Dates.Length == 2, "不完整日期被拒绝且原筛选不变");
        var clearPeer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(editor.ClearDateButtons["captured"]);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)clearPeer.GetPattern(Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
        await Task.Yield();
        Check(editor.Read([]).Dates.All(d => d.Field != "captured"), "两端清空才清除日期条件");
        Check(!editor.ClearDateButtons["captured"].IsEnabled, "原生清除按钮清空整个日期范围并禁用");
        editor.Ranges["width"].Min.Text = "500"; editor.Ranges["width"].Max.Text = "50"; rejected = false;
        try { editor.Read([]); } catch (ArgumentException) { rejected = true; }
        Check(rejected && !source.Ranges.ContainsKey("width"), "倒置数值范围拒绝应用且原筛选不变");
        var huge = new AdvancedFilterEditor(source with { Ranges = new() { ["allocatedBytes"] = new(9007199254740993, long.MaxValue) } });
        Check(huge.Read([]).Ranges["allocatedBytes"] == new IntRange(9007199254740993, long.MaxValue), "未编辑的大整数边界没有双精度舍入");
        huge.Ranges["allocatedBytes"].Min.Text = "1";
        bool exactOtherBound = false;
        try { exactOtherBound = huge.Read([]).Ranges["allocatedBytes"] == new IntRange(1, long.MaxValue); } catch (OverflowException) { }
        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var calendar = new Windows.Globalization.Calendar(); calendar.ChangeTimeZone("America/Los_Angeles");
        calendar.SetDateTime(AdvancedFilterEditor.CalendarValue("2024-05-06T12:00:00", "captureWall", timeZone: pacific));
        bool sameCalendarDay = calendar.Year == 2024 && calendar.Month == 5 && calendar.Day == 6;
        report["singleBoundKeepsOtherInt64"] = exactOtherBound; report["negativeZoneKeepsCalendarDay"] = sameCalendarDay;
        Check(exactOtherBound && sameCalendarDay, "只改单端保持另一端整数精度，负时区日历不回退一天");
        var integer = new AdvancedFilterEditor(source);
        integer.Ranges["allocatedBytes"].Min.Text = "9007199254740993";
        Check(integer.Read([]).Ranges["allocatedBytes"].Min == 9007199254740993, "新输入的大整数不经double截断");
        huge.Ranges["allocatedBytes"].Min.Text = "9007199254740992"; huge.Ranges["allocatedBytes"].Max.Text = "9007199254740993";
        Check(huge.Read([]).Ranges["allocatedBytes"] == new IntRange(9007199254740992, 9007199254740993), "相同double表示的不同整数仍能区分");
        huge.Ranges["allocatedBytes"].Min.Text = "";
        Check(huge.Read([]).Ranges["allocatedBytes"] == new IntRange(null, 9007199254740993), "仅清除最小值保留精确最大值");
        huge.Ranges["allocatedBytes"].Min.Text = "9007199254740993"; huge.Ranges["allocatedBytes"].Max.Text = "";
        Check(huge.Read([]).Ranges["allocatedBytes"] == new IntRange(9007199254740993, null), "仅清除最大值保留精确最小值");
        foreach (var zone in new[] { ("Pacific Standard Time", "America/Los_Angeles"), ("Eastern Standard Time", "America/New_York"), ("China Standard Time", "Asia/Shanghai") })
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(zone.Item1); calendar.ChangeTimeZone(zone.Item2);
            foreach (string clock in new[] { "utc", "captureWall" })
            {
                string value = clock == "utc" ? "2024-05-06T12:00:00Z" : "2024-05-06T12:00:00";
                calendar.SetDateTime(AdvancedFilterEditor.CalendarValue(value, clock, timeZone: timeZone));
                Check(calendar.Year == 2024 && calendar.Month == 5 && calendar.Day == 6, zone.Item2 + " " + clock + " 原生日历日期一致");
            }
        }
        var invalid = new AdvancedFilterEditor(source);
        invalid.Ranges["width"].Min.Text = "32"; invalid.Ranges["width"].Min.Text = "abc"; rejected = false;
        try { invalid.Read([]); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "无效数字文本不能静默采用旧数值");
        invalid.Ranges["width"].Min.Text = "64";
        Check(invalid.Read([]).Ranges["width"].Min == 64, "尚未失焦的有效文本按新数值应用");
        invalid.Ranges["width"].Min.Text = "";
        Check(!invalid.Read([]).Ranges.ContainsKey("width"), "尚未失焦的空文本清除旧条件");
        foreach (string text in new[] { "1.5", "9007199254740993.5", "9223372036854775808" })
        {
            invalid.Ranges["width"].Min.Text = text; rejected = false;
            try { invalid.Read([]); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "整数输入拒绝小数或溢出：" + text);
        }
        Check(Application.Current.Resources["SystemFillColorCriticalBrush"] is Microsoft.UI.Xaml.Media.Brush, "原生错误提示画刷可解析");

        foreach (int width in new[] { 320, 520 })
        {
            var layout = new AdvancedFilterEditor(source with { Kinds = ["video"], Ranges = new() { ["allocatedBytes"] = new(9007199254740993, long.MaxValue) } });
            foreach (var expander in layout.View.Children.OfType<Expander>()) expander.IsExpanded = true;
            var scroll = new ScrollViewer { Content = layout.View, Width = width, MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var dialog = new ContentDialog { XamlRoot = Shell.XamlRoot, Title = "更多筛选", Content = scroll, PrimaryButtonText = "应用筛选", CloseButtonText = "取消" };
            dialog.Resources["ContentDialogMaxWidth"] = 640d;
            var shown = dialog.ShowAsync();
            try
            {
                await WaitUntil(() => layout.View.IsLoaded, TimeSpan.FromSeconds(3)); dialog.UpdateLayout();
                var hostBounds = new List<object>();
                for (DependencyObject? parent = VisualTreeHelper.GetParent(scroll); parent is not null && parent != dialog; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is not FrameworkElement host || host.ActualWidth <= 0) continue;
                    var bounds = scroll.TransformToVisual(host).TransformBounds(new(0, 0, scroll.ActualWidth, scroll.ActualHeight));
                    hostBounds.Add(new { host.Name, host.ActualWidth, bounds.X, bounds.Right });
                    Check(bounds.X >= -1 && bounds.Right <= host.ActualWidth + 1, $"{width} DIP 对话框父容器没有裁切内容");
                }
                report[$"layoutHosts{width}"] = hostBounds;
                Check(layout.Read([]).Dates.SequenceEqual(source.Dates), $"{width} DIP 原生模板加载后日期精确条件保留");
                Check(layout.Read([]).Ranges["allocatedBytes"] == new IntRange(9007199254740993, long.MaxValue), $"{width} DIP 模板加载后整数显示和数值不舍入");
                Check(layout.View.ActualWidth > 200 && layout.View.ActualWidth <= width + 1, $"{width} DIP 原生面板宽度受窗口约束");
                foreach (var pair in layout.Ranges.Values)
                    foreach (var box in new[] { pair.Min, pair.Max })
                    {
                        var bounds = box.TransformToVisual(layout.View).TransformBounds(new(0, 0, box.ActualWidth, box.ActualHeight));
                        Check(bounds.X >= 0 && bounds.Right <= layout.View.ActualWidth + 1 && box.ActualWidth >= 70, $"{width} DIP 数值输入没有横向溢出");
                    }
                var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(dialog);
                using var memory = new InMemoryRandomAccessStream(); var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, memory);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, (await bitmap.GetPixelsAsync()).ToArray()); await encoder.FlushAsync();
                memory.Seek(0); using var input = memory.AsStreamForRead(); using var output = File.Create(Path.Combine(dataDirectory, $"filters-{width}.png")); await input.CopyToAsync(output);
            }
            finally { dialog.Hide(); await shown; }
        }
        report["status"] = "PASS";
    }
}
