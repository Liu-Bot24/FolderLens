namespace FolderLens.Core;

/// <summary>Tracks folder transitions independently of repeated image redraws.</summary>
public sealed class ViewerGroupBoundary
{
    private (string Session, string Group)? previous;

    public bool Observe(bool fullScreen, string? session, string? group)
    {
        if (!fullScreen || session is null || group is null)
        {
            previous = null;
            return false;
        }
        var current = (session, group);
        if (previous == current) return false;
        previous = current;
        return true;
    }
}
