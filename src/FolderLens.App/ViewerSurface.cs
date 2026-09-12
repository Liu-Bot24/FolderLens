using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace FolderLens.App;

public enum ViewerCursorFeedback { Arrow,Pan }

public sealed class ViewerSurface : Grid
{
    private readonly InputCursor arrow=InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    private readonly InputCursor hand=InputSystemCursor.Create(InputSystemCursorShape.Hand);
    private readonly Canvas overlay=new(){IsHitTestVisible=false};
    private readonly CanvasControl lensCanvas=new(){IsHitTestVisible=false,UseSharedDevice=true};
    private readonly TextBlock lensLabel=new(){FontSize=11,Margin=new Thickness(5),Foreground=new SolidColorBrush(Microsoft.UI.Colors.White),VerticalAlignment=VerticalAlignment.Bottom,IsHitTestVisible=false};
    private readonly Border lens;
    private bool cursorHidden;
    private ViewerCursorFeedback cursor=ViewerCursorFeedback.Arrow;
    public CursorInputSurface? InputSurface {get;set;}
    public event Action<CanvasControl,CanvasDrawEventArgs>? MagnifierDraw;
    public event Action? MagnifierResourcesReset;
    public double MagnifierWidth=>lens.Width;
    public double MagnifierHeight=>lens.Height;
    public ViewerSurface()
    {
        var content=new Grid();content.Children.Add(lensCanvas);content.Children.Add(lensLabel);
        lens=new Border{Child=content,BorderBrush=new SolidColorBrush(Microsoft.UI.Colors.White),BorderThickness=new Thickness(2),Background=new SolidColorBrush(Microsoft.UI.Colors.Black),Visibility=Visibility.Collapsed,IsHitTestVisible=false};
        lensCanvas.Draw+=(sender,args)=>MagnifierDraw?.Invoke(sender,args);
        lensCanvas.CreateResources+=(_,args)=>{if(args.Reason==Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesReason.NewDevice)MagnifierResourcesReset?.Invoke();};
        overlay.Children.Add(lens);Canvas.SetZIndex(overlay,1000);Children.Add(overlay);
    }
    public void SetCursorHidden(bool hidden){cursorHidden=hidden;ApplyCursor();}
    public void SetViewerCursor(ViewerCursorFeedback feedback){cursor=feedback;ApplyCursor();}
    private void ApplyCursor()
    {
        ProtectedCursor=cursorHidden?null:cursor==ViewerCursorFeedback.Pan?hand:arrow;
        InputSurface?.SetCursor(ProtectedCursor);
    }
    public void ShowMagnifier(Point point,double width,double height,bool wholeImage=false)
    {
        lens.Width=width;lens.Height=height;
        lens.BorderThickness=new Thickness(wholeImage?0:2);
        Canvas.SetLeft(lens,wholeImage?0:Math.Clamp(point.X+18,0,Math.Max(0,ActualWidth-width)));
        Canvas.SetTop(lens,wholeImage?0:Math.Clamp(point.Y+18,0,Math.Max(0,ActualHeight-height)));
        lensCanvas.Clip=new RectangleGeometry{Rect=new Rect(0,0,width,height)};
        lens.Visibility=Visibility.Visible;lensCanvas.Invalidate();
    }
    public void SetMagnifierQuality(string label){lensLabel.Text=label;lensCanvas.Invalidate();}
    public void InvalidateMagnifier()=>lensCanvas.Invalidate();
    public void HideMagnifier(){lens.Visibility=Visibility.Collapsed;}
}
