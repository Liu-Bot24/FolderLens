using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private double treeFraction=.6;
    private bool resizingPreview;
    private void SetPreviewSplit(double fraction)
    {
        treeFraction=double.IsFinite(fraction)?Math.Clamp(fraction,.12,.82):.6;
        TreeRow.Height=new GridLength(treeFraction,GridUnitType.Star);
        PreviewRow.Height=new GridLength(1-treeFraction,GridUnitType.Star);
    }
    private void StartPreviewResize(object sender,PointerRoutedEventArgs e)
    {
        if(immersive||!e.GetCurrentPoint(BodyGrid).Properties.IsLeftButtonPressed)return;
        resizingPreview=PreviewHeightDivider.CapturePointer(e.Pointer);e.Handled=true;
    }
    private void ResizePreview(object sender,PointerRoutedEventArgs e)
    {
        if(!resizingPreview||immersive)return;
        SetPreviewSplit(e.GetCurrentPoint(BodyGrid).Position.Y/Math.Max(1,BodyGrid.ActualHeight-6));e.Handled=true;
    }
    private void EndPreviewResize(object sender,PointerRoutedEventArgs e)
    {resizingPreview=false;PreviewHeightDivider.ReleasePointerCapture(e.Pointer);e.Handled=true;}
    private void PreviewResizeLost(object sender,PointerRoutedEventArgs e)=>resizingPreview=false;
    private void ResetPreviewSplit(object sender,DoubleTappedRoutedEventArgs e){SetPreviewSplit(.6);e.Handled=true;}
}
