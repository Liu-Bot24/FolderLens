using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed record ExtensionOption(string Extension)
    {
        public string Label=>Extension.Length==0?"无扩展名":"."+Extension.ToUpperInvariant();
    }
    private string[] selectedFileExtensions=[];
    private string[] savedContentFormats=[];
    private bool updatingFormatChoices;
    private CancellationTokenSource? formatChoicesStop;
    private void OpenFilterPanel(object sender,object args)=>ArrangeFilterPanel();
    private void ArrangeFilterPanel()
    {
        if(Shell.XamlRoot is not {} xaml)return;
        FilterPanel.Width=Math.Max(220,Math.Min(520,xaml.Size.Width-48));
        FilterPanel.MaxHeight=Math.Max(180,Math.Min(650,xaml.Size.Height-100));
    }
    private async void OpenFormatChoices(object sender,object args)=>await LoadFormatChoices();
    private void CloseFormatChoices(object sender,object args)=>formatChoicesStop?.Cancel();
    private async Task LoadFormatChoices()
    {
        formatChoicesStop?.Cancel();
        using var work=browserWork.Enter();if(work is null||closing)return;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        formatChoicesStop=stop;stop.CancelAfter(TimeSpan.FromSeconds(6));
        long revision=rootChangeVersion,currentGeneration=generation;var store=catalog;
        FormatOptions.IsEnabled=false;FormatChoicesStatus.Text="正在读取格式…";
        try
        {
            if(store is null||rootId.Length==0){FormatChoicesStatus.Text="请先打开文件夹。";return;}
            var choices=await store.ReadFileExtensions(CurrentFilter(),stop.Token);
            if(closing||revision!=rootChangeVersion||stop.IsCancellationRequested)return;
            if(currentGeneration!=generation){FormatChoicesStatus.Text="目录范围已变化，请重新打开格式列表。";return;}
            updatingFormatChoices=true;
            try
            {
                var options=choices.Values.Concat(selectedFileExtensions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(value=>new ExtensionOption(value)).ToArray();
                FormatOptions.ItemsSource=options;
                foreach(var item in options)if(selectedFileExtensions.Contains(item.Extension))FormatOptions.SelectedItems.Add(item);
                FormatChoicesStatus.Text=choices.Truncated?"格式较多，仅列出前 1024 种。":options.Length==0?"当前范围暂无文件格式。":"选择一种或多种格式";
                FormatOptions.IsEnabled=true;
            }
            finally{updatingFormatChoices=false;}
        }
        catch(OperationCanceledException){if(!closing&&revision==rootChangeVersion)FormatChoicesStatus.Text="读取已取消，请重新打开格式列表。";}
        catch(Exception ex){if(!closing&&revision==rootChangeVersion)FormatChoicesStatus.Text="无法读取格式，请重新打开列表重试。";RecordWebView("FormatChoicesError "+ex.GetType().Name+":"+ex.HResult);}
        finally{if(ReferenceEquals(formatChoicesStop,stop))formatChoicesStop=null;}
    }
    private void FormatSelectionChanged(object sender,SelectionChangedEventArgs args)
    {
        if(updatingFormatChoices)return;
        if(FormatOptions.SelectedItems.Count>64)
        {
            updatingFormatChoices=true;
            try{foreach(var item in args.AddedItems)FormatOptions.SelectedItems.Remove(item);}
            finally{updatingFormatChoices=false;}
            FormatChoicesStatus.Text="最多选择 64 种格式。";return;
        }
        selectedFileExtensions=FormatOptions.SelectedItems.Cast<ExtensionOption>().Select(item=>item.Extension).Order(StringComparer.Ordinal).ToArray();
        savedContentFormats=[];UpdateFormatPickerLabel();
    }
    private void SetFormatChoices(FilterSpec filter)
    {
        selectedFileExtensions=filter.FileExtensions.ToArray();savedContentFormats=filter.Formats.ToArray();
        updatingFormatChoices=true;
        try
        {
            FormatOptions.SelectedItems.Clear();
            foreach(var item in FormatOptions.Items.Cast<ExtensionOption>())if(selectedFileExtensions.Contains(item.Extension))FormatOptions.SelectedItems.Add(item);
        }
        finally{updatingFormatChoices=false;}
        UpdateFormatPickerLabel();
    }
    private void UpdateFormatPickerLabel()
    {
        FormatPicker.Content=selectedFileExtensions.Length>3?$"已选 {selectedFileExtensions.Length} 种格式":selectedFileExtensions.Length>0?string.Join("、",selectedFileExtensions.Select(value=>new ExtensionOption(value).Label)):
            savedContentFormats.Length>0?string.Join("、",savedContentFormats.Select(value=>value.ToUpperInvariant()))+"（已保存）":"全部格式";
    }
    private void ClearFormatChoices(object sender,RoutedEventArgs args)
    {
        updatingFormatChoices=true;try{FormatOptions.SelectedItems.Clear();}finally{updatingFormatChoices=false;}
        SetFormatChoices(new());
    }
}
