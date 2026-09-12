using System.Globalization;
using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

// The dialog owns a draft. Only a fully validated draft is applied to the browser.
internal sealed class AdvancedFilterEditor
{
    private readonly FilterSpec original;
    internal StackPanel View { get; } = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
    internal Dictionary<string, (TextBox Min, TextBox Max)> Ranges { get; } = [];
    internal Dictionary<string, (CalendarDatePicker From, CalendarDatePicker To)> Dates { get; } = [];
    internal Dictionary<string, Button> ClearDateButtons { get; } = [];
    private readonly Dictionary<string, (DateTimeOffset? From, DateTimeOffset? To)> initialDates = [];
    private readonly NumberBox ratioMin = Number("最小宽高比"), ratioMax = Number("最大宽高比");
    private readonly NumberBox fpsMin = Number("最低帧率"), fpsMax = Number("最高帧率");
    private readonly ComboBox orientation = new() { Header = "画面方向", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox videoCodec = new() { Header = "视频编码", PlaceholderText = "例如 h264, hevc" };
    private readonly TextBox audioCodec = new() { Header = "音频编码", PlaceholderText = "例如 aac, opus" };

    internal AdvancedFilterEditor(FilterSpec filter)
    {
        original = filter;
        bool all = filter.Kinds.Length == 0;
        bool visual = all || filter.Kinds.Any(k => k is "image" or "video");
        bool video = all || filter.Kinds.Contains("video");
        bool audio = video || filter.Kinds.Contains("audio");
        View.Children.Add(new TextBlock { Text = "只显示适用于当前类型的条件；已设置的其他条件仍会保留。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var size = Section("文件占用", true);
        AddRange(size, "allocatedBytes", "磁盘占用（字节）");

        string[] dimensions = ["width", "height", "longEdge", "shortEdge", "pixelCount"];
        if (visual || dimensions.Any(filter.Ranges.ContainsKey) || filter.AspectRatio is not null || filter.Orientation != "any")
        {
            var picture = Section("画面尺寸", false);
            if (!visual) ExistingConditionNotice(picture);
            foreach (var field in new[] { ("width", "宽度（像素）"), ("height", "高度（像素）"), ("longEdge", "长边（像素）"), ("shortEdge", "短边（像素）"), ("pixelCount", "像素总数") })
                if (visual || filter.Ranges.ContainsKey(field.Item1)) AddRange(picture, field.Item1, field.Item2);
            SetRange(ratioMin, ratioMax, filter.AspectRatio);
            if (visual || filter.AspectRatio is not null) picture.Children.Add(Pair("宽高比", ratioMin, ratioMax));
            foreach (var item in new[] { ("不限", "any"), ("横向", "landscape"), ("正方形", "square"), ("竖向", "portrait") })
                orientation.Items.Add(new ComboBoxItem { Content = item.Item1, Tag = item.Item2 });
            orientation.SelectedItem = orientation.Items.Cast<ComboBoxItem>().Single(i => (string)i.Tag == filter.Orientation);
            if (visual || filter.Orientation != "any") picture.Children.Add(orientation);
        }

        SetRange(fpsMin, fpsMax, filter.FrameRate);
        videoCodec.Text = string.Join(", ", filter.VideoCodecs); audioCodec.Text = string.Join(", ", filter.AudioCodecs);
        if (audio || filter.Ranges.ContainsKey("durationMs") || filter.FrameRate is not null || filter.VideoCodecs.Length > 0 || filter.AudioCodecs.Length > 0)
        {
            var playback = Section("播放信息", false);
            if (!audio) ExistingConditionNotice(playback);
            if (audio || filter.Ranges.ContainsKey("durationMs")) AddRange(playback, "durationMs", "时长（毫秒）");
            if (video || filter.FrameRate is not null) playback.Children.Add(Pair("帧率", fpsMin, fpsMax));
            if (video || filter.VideoCodecs.Length > 0) playback.Children.Add(videoCodec);
            if (audio || filter.AudioCodecs.Length > 0) playback.Children.Add(audioCodec);
        }
        var datePanel = Section("日期", false);
        foreach (var field in new[] { ("modified", "修改日期"), ("created", "创建日期"), ("captured", "拍摄日期") })
        {
            var saved = filter.Dates.SingleOrDefault(d => d.Field == field.Item1);
            if (field.Item1 == "captured" && !visual && saved is null) continue;
            var from = Calendar(field.Item1 + "From", field.Item2 + "起始日期");
            var to = Calendar(field.Item1 + "To", field.Item2 + "结束日期（含当天）");
            if (saved is not null)
            {
                from.Date = CalendarValue(saved.StartInclusive, saved.Clock);
                // Exclusive upper bounds may contain a time: show the last included day.
                to.Date = CalendarValue(saved.EndExclusive, saved.Clock, endExclusive: true);
            }
            Dates[field.Item1] = (from, to); initialDates[field.Item1] = (from.Date, to.Date);
            var line = Pair(field.Item2, from, to);
            var heading = new Grid(); heading.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock { Text = field.Item2, VerticalAlignment = VerticalAlignment.Center });
            var clear = new Button { Content = "清除", Padding = new Thickness(8, 2, 8, 2), MinHeight = 28, IsEnabled = from.Date is not null || to.Date is not null };
            AutomationProperties.SetName(clear, "清除" + field.Item2 + "范围"); AutomationProperties.SetAutomationId(clear, "AdvancedFilter." + field.Item1 + ".Clear");
            ToolTipService.SetToolTip(clear, "清除" + field.Item2 + "范围");
            clear.Click += (_, _) => { from.Date = null; to.Date = null; };
            from.DateChanged += (_, _) => clear.IsEnabled = from.Date is not null || to.Date is not null;
            to.DateChanged += (_, _) => clear.IsEnabled = from.Date is not null || to.Date is not null;
            ClearDateButtons[field.Item1] = clear; Grid.SetColumn(clear, 1); heading.Children.Add(clear);
            line.Children[0] = heading; datePanel.Children.Add(line);
        }
    }

    internal StackPanel Section(string title, bool expanded)
    {
        var content = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        var expander = new Expander { Header = title, Content = content, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(expander, "AdvancedFilter." + title);
        View.Children.Add(expander); return content;
    }

    private void AddRange(StackPanel panel, string key, string label)
    {
        var min = Integer(label + "最小值"); var max = Integer(label + "最大值");
        original.Ranges.TryGetValue(key, out var range);
        min.Text = range?.Min?.ToString(CultureInfo.CurrentCulture) ?? ""; max.Text = range?.Max?.ToString(CultureInfo.CurrentCulture) ?? "";
        AutomationProperties.SetAutomationId(min, "AdvancedFilter." + key + ".Min");
        AutomationProperties.SetAutomationId(max, "AdvancedFilter." + key + ".Max");
        Ranges[key] = (min, max); panel.Children.Add(Pair(label, min, max));
    }
    private static void ExistingConditionNotice(StackPanel panel) => panel.Children.Add(new TextBlock { Text = "以下是已设置的条件，可能使当前类型没有匹配结果。可留空清除。", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
    private static TextBox Integer(string name)
    {
        var scope = new Microsoft.UI.Xaml.Input.InputScope(); scope.Names.Add(new Microsoft.UI.Xaml.Input.InputScopeName { NameValue = Microsoft.UI.Xaml.Input.InputScopeNameValue.Number });
        var box = new TextBox { PlaceholderText = "不限", InputScope = scope, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, name); return box;
    }
    private static NumberBox Number(string name)
    {
        var box = new NumberBox { Minimum = 0, Value = double.NaN, PlaceholderText = "不限", HorizontalAlignment = HorizontalAlignment.Stretch, ValidationMode = NumberBoxValidationMode.Disabled };
        AutomationProperties.SetName(box, name); return box;
    }
    private static CalendarDatePicker Calendar(string id, string name)
    {
        var picker = new CalendarDatePicker { PlaceholderText = name.Contains("起始") ? "起始日期" : "结束日期（含当天）", HorizontalAlignment = HorizontalAlignment.Stretch, MinDate = new DateTimeOffset(1, 1, 1, 0, 0, 0, TimeSpan.Zero), MaxDate = new DateTimeOffset(9999, 12, 31, 0, 0, 0, TimeSpan.Zero) };
        AutomationProperties.SetName(picker, name); AutomationProperties.SetAutomationId(picker, "AdvancedFilter." + id); return picker;
    }
    private static StackPanel Pair(string title, FrameworkElement left, FrameworkElement right)
    {
        if (left is NumberBox minimum) minimum.Header = "最小值";
        if (right is NumberBox maximum) maximum.Header = "最大值";
        if (left is TextBox minimumInteger) minimumInteger.Header = "最小值";
        if (right is TextBox maximumInteger) maximumInteger.Header = "最大值";
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1); row.Children.Add(left); row.Children.Add(right);
        var panel = new StackPanel { Spacing = 6 }; panel.Children.Add(new TextBlock { Text = title }); panel.Children.Add(row); return panel;
    }
    private static void SetValue(NumberBox box, double? value)
    {
        box.Value = value ?? double.NaN;
        // Collapsed expanders may not create their content templates before Apply.
        box.Text = value is { } number ? box.NumberFormatter.FormatDouble(number) : "";
    }
    private static void SetRange(NumberBox min, NumberBox max, NumberRange? range) { SetValue(min, range?.Min); SetValue(max, range?.Max); }
    internal static DateTimeOffset CalendarValue(string value, string clock, bool endExclusive = false, TimeZoneInfo? timeZone = null)
    {
        timeZone ??= TimeZoneInfo.Local;
        long ticks = FilterSpec.DateTicks(value, clock);
        if (endExclusive) ticks--;
        var date = clock == "utc" ? TimeZoneInfo.ConvertTimeFromUtc(new DateTime(ticks, DateTimeKind.Utc), timeZone).Date : new DateTime(ticks).Date;
        return new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Unspecified), timeZone.GetUtcOffset(date));
    }
    private static double? Value(NumberBox box)
    {
        if (string.IsNullOrWhiteSpace(box.Text)) return null;
        // Text can still be uncommitted when Apply is invoked with the keyboard.
        // Use the control's locale-aware parser instead of falling back to its old Value.
        var parsed = (box.NumberFormatter as Windows.Globalization.NumberFormatting.INumberParser)?.ParseDouble(box.Text);
        if (parsed is null) throw new ArgumentException("请输入有效数字，或留空表示不限。");
        return double.IsFinite(box.Value) && box.Text == box.NumberFormatter.FormatDouble(box.Value) ? box.Value : parsed;
    }
    private static NumberRange? Range(NumberBox min, NumberBox max) => (Value(min), Value(max)) is (null, null) ? null : new(Value(min), Value(max));

    internal FilterSpec Read(ExclusionSpec[] exclusions)
    {
        var values = new Dictionary<string, IntRange>(original.Ranges);
        foreach (var (key, boxes) in Ranges)
        {
            long? Integral(TextBox box)
            {
                if (string.IsNullOrWhiteSpace(box.Text)) return null;
                // Integer bounds never pass through NumberBox.Value (double), including display.
                if (long.TryParse(box.Text, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out long value)) return value;
                throw new ArgumentException("文件大小、像素和毫秒必须是有效整数，且不要超过支持的最大值。");
            }
            var min = Integral(boxes.Min); var max = Integral(boxes.Max);
            if (min is null && max is null) values.Remove(key); else values[key] = new(min, max);
        }
        var dates = original.Dates.ToDictionary(d => d.Field);
        foreach (var (key, boxes) in Dates)
        {
            if ((boxes.From.Date, boxes.To.Date) == initialDates[key]) continue; // Preserve exact clock and sub-day bounds on an unchanged draft.
            if (boxes.From.Date is null && boxes.To.Date is null) { dates.Remove(key); continue; }
            if (boxes.From.Date is not { } start || boxes.To.Date is not { } end) throw new ArgumentException("日期范围需要同时选择起始和结束日期。清除时请将两项都留空。");
            string clock = dates.GetValueOrDefault(key)?.Clock ?? (key == "captured" ? "captureWall" : "utc");
            string Format(DateTime date) => clock == "captureWall" ? date.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : DateTime.SpecifyKind(date, DateTimeKind.Local).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            dates[key] = new(key, clock, Format(start.Date), Format(end.Date.AddDays(1)));
        }
        var candidate = original with { Ranges = values, Dates = dates.Values.ToArray(), AspectRatio = Range(ratioMin, ratioMax), Orientation = (orientation.SelectedItem as ComboBoxItem)?.Tag as string ?? original.Orientation, FrameRate = Range(fpsMin, fpsMax), VideoCodecs = videoCodec.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), AudioCodecs = audioCodec.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), Exclusions = exclusions };
        candidate.Validate(); return candidate;
    }
}
