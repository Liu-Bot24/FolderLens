using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool sortDescending;
    private void UpdateSortOptions()
    {
        string category=Tag(Category),selectedField=SortField.SelectedItem is ComboBoxItem item?item.Tag.ToString()!:"name";
        bool previous=suppressFilters;suppressFilters=true;
        try
        {
            SortField.Items.Clear();
            foreach(var option in new[]{("名称","name"),("文件路径","path"),("格式","format"),("大小","logicalBytes"),("修改时间","modified"),("宽度","width"),("高度","height"),("像素数","pixelCount"),("时长","durationMs")})
                if(BrowserSortOptions.IsApplicable(category,option.Item2))SortField.Items.Add(new ComboBoxItem{Content=option.Item1,Tag=option.Item2});
            SelectTag(SortField,BrowserSortOptions.IsApplicable(category,selectedField)?selectedField:"name");
        }
        finally{suppressFilters=previous;}
        UpdateDetailSort();
    }
    private async void ChangeSortDirection(object sender,RoutedEventArgs args)
    {
        sortDescending=!sortDescending;UpdateDetailSort();await RefreshQuery(preserveViewport:true);
    }
    private void UpdateSortDirectionIndicator()
    {
        SortDirectionIcon.RenderTransformOrigin=new Windows.Foundation.Point(.5,.5);
        SortDirectionIcon.RenderTransform=new Microsoft.UI.Xaml.Media.RotateTransform{Angle=sortDescending?180:0};
        ToolTipService.SetToolTip(Descending,sortDescending?"当前降序（从高到低）；点击改为升序":"当前升序（从低到高）；点击改为降序");
        AutomationProperties.SetName(Descending,sortDescending?"降序排列，点击改为升序":"升序排列，点击改为降序");
    }
}
