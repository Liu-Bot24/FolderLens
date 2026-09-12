namespace FolderLens.Core;

public sealed record PressZoomOptions(string LargeViewMode="whole",double Percent=250)
{
    public PressZoomOptions Normalize()=>new(LargeViewMode=="lens"?"lens":"whole",double.IsFinite(Percent)?Math.Clamp(Percent,25,800):250);
    public bool UsesWholeImage(bool largeView)=>largeView&&LargeViewMode!="lens";
    public static (double X,double Y) ClampCenter(double x,double y,double sourceWidth,double sourceHeight,double viewportWidth,double viewportHeight,double scale,int rotation)
    {
        double halfX=(rotation%2==0?viewportWidth:viewportHeight)/scale/2,halfY=(rotation%2==0?viewportHeight:viewportWidth)/scale/2;
        return(sourceWidth<=2*halfX?sourceWidth/2:Math.Clamp(x,halfX,sourceWidth-halfX),sourceHeight<=2*halfY?sourceHeight/2:Math.Clamp(y,halfY,sourceHeight-halfY));
    }
}
