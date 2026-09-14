using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace FolderLens.App;

public interface INavigationNodePresentation
{
    string NavigationLabel {get;}
    string NavigationGlyph {get;}
}

public sealed class NavigationNodeConverter : IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,string language)
    {
        var content=value is TreeViewNode node?node.Content:value;
        if(content is INavigationNodePresentation item)return parameter as string=="glyph"?item.NavigationGlyph:item.NavigationLabel;
        return parameter as string=="glyph"?"\uE8B7":content?.ToString()??"";
    }
    public object ConvertBack(object value,Type targetType,object parameter,string language)=>throw new NotSupportedException();
}
