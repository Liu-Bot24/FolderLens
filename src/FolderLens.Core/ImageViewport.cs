namespace FolderLens.Core;

public readonly record struct TileRange(int FirstX,int FirstY,int LastX,int LastY)
{
    public long Count=>LastX<FirstX||LastY<FirstY?0:checked((long)(LastX-FirstX+1)*(LastY-FirstY+1));
}

public static class ImageViewport
{
    // Pan is expressed in the unrotated image axes, matching the drawing transform.
    public static TileRange VisibleTiles(double sourceWidth,double sourceHeight,double viewportWidth,double viewportHeight,double scale,double panX,double panY,int quarterTurns,int tileSize=1024,int padding=1)
    {
        if(new[]{sourceWidth,sourceHeight,viewportWidth,viewportHeight,scale}.Any(v=>!double.IsFinite(v)||v<=0)||!double.IsFinite(panX)||!double.IsFinite(panY)||tileSize<1||padding<0)throw new ArgumentOutOfRangeException(nameof(scale));
        int turn=(quarterTurns%4+4)%4;
        double minX=double.PositiveInfinity,minY=minX,maxX=double.NegativeInfinity,maxY=maxX;
        foreach(var point in new[]{(-viewportWidth/2,-viewportHeight/2),(viewportWidth/2,-viewportHeight/2),(-viewportWidth/2,viewportHeight/2),(viewportWidth/2,viewportHeight/2)})
        {
            var p=turn switch{1=>(point.Item2,-point.Item1),2=>(-point.Item1,-point.Item2),3=>(-point.Item2,point.Item1),_=>(point.Item1,point.Item2)};
            double x=(p.Item1-panX)/scale+sourceWidth/2,y=(p.Item2-panY)/scale+sourceHeight/2;
            minX=Math.Min(minX,x);minY=Math.Min(minY,y);maxX=Math.Max(maxX,x);maxY=Math.Max(maxY,y);
        }
        int lastX=checked((int)Math.Ceiling(sourceWidth/tileSize)-1),lastY=checked((int)Math.Ceiling(sourceHeight/tileSize)-1);
        return new(Math.Clamp(checked((int)Math.Floor(minX/tileSize))-padding,0,lastX),Math.Clamp(checked((int)Math.Floor(minY/tileSize))-padding,0,lastY),Math.Clamp(checked((int)Math.Ceiling(maxX/tileSize)-1)+padding,0,lastX),Math.Clamp(checked((int)Math.Ceiling(maxY/tileSize)-1)+padding,0,lastY));
    }
}
