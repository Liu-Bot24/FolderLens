using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

// The cursor belongs to the actual hit-tested input element, not the Win2D child.
public sealed class CursorInputSurface : Grid
{
    public void SetCursor(InputCursor? cursor)=>ProtectedCursor=cursor;
}
public sealed class ResizeDivider : Grid
{
    public ResizeDivider()=>ProtectedCursor=InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}
public sealed class VerticalResizeDivider : Grid
{
    public VerticalResizeDivider()=>ProtectedCursor=InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
}
