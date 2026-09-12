using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Windows.System;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private sealed class DetailColumn(string title,string field,double initialWidth):INotifyPropertyChanged
    {
        public string Title {get;}=title;
        public string Field {get;}=field;
        public double DefaultWidth {get;}=initialWidth;
        private double width=initialWidth;
        public GridLength Width=>new(width);
        public void Resize(double value)
        {
            width=double.IsFinite(value)?Math.Clamp(value,64,1200):DefaultWidth;
            PropertyChanged?.Invoke(this,new(nameof(Width)));
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly DetailColumn[] detailColumns=[new("名称","name",210),new("相对路径","path",300),new("格式","format",80),new("尺寸","pixelCount",120),new("文件大小","logicalBytes",105),new("磁盘占用","allocatedBytes",105),new("修改时间","modified",155),new("时长","durationMs",100)];
    private readonly List<Button> detailSortButtons=[];
    private readonly SemaphoreSlim detailSettingsGate=new(1,1);
    private ScrollViewer? detailScroll;

    private void InitializeDetails()
    {
        BindDetailColumns(DetailsHeader);
        for(int i=0;i<detailColumns.Length;i++)
        {
            var column=detailColumns[i];
            var cell=new Grid();Grid.SetColumn(cell,i);DetailsHeader.Children.Add(cell);
            var button=new Button{Content=column.Title,HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Left,Margin=new(0,0,8,0),Padding=new(5,4,2,4)};
            AutomationProperties.SetAutomationId(button,"Sort_"+column.Field);AutomationProperties.SetName(button,"按"+column.Title+"排序");
            ToolTipService.SetToolTip(button,column.Field=="pixelCount"?"按像素总数排序；再次点击切换升序 / 降序":"按"+column.Title+"排序；再次点击切换升序 / 降序");
            button.Click+=async(_,_)=>
            {
                bool same=Tag(SortField)==column.Field;suppressFilters=true;
                try{SelectTag(SortField,column.Field);sortDescending=same?!sortDescending:false;}
                finally{suppressFilters=false;}
                UpdateDetailSort();await RefreshQuery(preserveViewport:true);
            };
            detailSortButtons.Add(button);cell.Children.Add(button);
            var grip=new Thumb{Width=8,HorizontalAlignment=HorizontalAlignment.Right,Style=(Style)Shell.Resources["ColumnResizeGrip"],IsTabStop=true};
            AutomationProperties.SetAutomationId(grip,"Resize_"+column.Field);AutomationProperties.SetName(grip,"调整"+column.Title+"列宽");
            ToolTipService.SetToolTip(grip,"拖动调整列宽；键盘左右键每次调整 16，Home 恢复默认宽度");
            grip.DragDelta+=(_,e)=>column.Resize(column.Width.Value+e.HorizontalChange);
            grip.DragCompleted+=async(_,_)=>await SaveDetailWidths();
            grip.KeyDown+=async(_,e)=>
            {
                if(e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Home))return;
                e.Handled=true;column.Resize(e.Key==VirtualKey.Home?column.DefaultWidth:column.Width.Value+(e.Key==VirtualKey.Left?-16:16));await SaveDetailWidths();
            };
            cell.Children.Add(grip);
        }
        SortField.SelectionChanged+=(_,_)=>UpdateDetailSort();Descending.Click+=(_,_)=>UpdateDetailSort();
        FilesList.Loaded+=(_,_)=>EnsureDetailScroll();
        FilesList.SizeChanged+=(_,_)=>EnsureDetailScroll();
        UpdateSortOptions();
    }
    private void DetailScrollChanged(object? sender,ScrollViewerViewChangedEventArgs e)
    {
        if(detailScroll is null)return;
        DetailsHeaderScroll.ChangeView(detailScroll.HorizontalOffset,null,null,true);
        if(Environment.GetCommandLineArgs().Contains("--diagnostic-ui"))RecordWebView($"DetailsScroll list={detailScroll.HorizontalOffset} header={DetailsHeaderScroll.HorizontalOffset}");
    }
    private void EnsureDetailScroll()
    {
        var scroll=FindScrollViewer(FilesList);if(scroll is null||ReferenceEquals(scroll,detailScroll))return;
        if(detailScroll is not null)detailScroll.ViewChanged-=DetailScrollChanged;
        detailScroll=scroll;detailScroll.ViewChanged+=DetailScrollChanged;DetailScrollChanged(null,null!);
    }
    private void DetailRowLoaded(object sender,RoutedEventArgs e){BindDetailColumns((Grid)sender);EnsureDetailScroll();}
    private void BindDetailColumns(Grid grid)
    {
        grid.ColumnDefinitions.Clear();
        foreach(var column in detailColumns)
        {
            var definition=new ColumnDefinition();
            BindingOperations.SetBinding(definition,ColumnDefinition.WidthProperty,new Binding{Source=column,Path=new PropertyPath(nameof(DetailColumn.Width)),Mode=BindingMode.OneWay});
            grid.ColumnDefinitions.Add(definition);
        }
    }
    private void UpdateDetailSort()
    {
        if(SortField.SelectedItem is not ComboBoxItem)return;
        UpdateSortDirectionIndicator();
        for(int i=0;i<detailSortButtons.Count;i++)
        {
            detailSortButtons[i].IsEnabled=FolderLens.Core.BrowserSortOptions.IsApplicable(Tag(Category),detailColumns[i].Field);
            detailSortButtons[i].Content=detailColumns[i].Title+(Tag(SortField)==detailColumns[i].Field?(sortDescending?" ↓":" ↑"):"");
        }
    }
    private async Task RestoreDetailWidths()
    {
        if(settings is null)return;
        if(await settings.Load<Dictionary<string,double>>("detail-columns.json") is {} widths)
            foreach(var column in detailColumns)if(widths.TryGetValue(column.Field,out var width))column.Resize(width);
    }
    private async Task SaveDetailWidths()
    {
        if(settings is null)return;
        await detailSettingsGate.WaitAsync();
        try{await settings.Save("detail-columns.json",detailColumns.ToDictionary(c=>c.Field,c=>c.Width.Value));}
        catch(Exception ex){if(!closing)ShowError(ex);}
        finally{detailSettingsGate.Release();}
    }
}
