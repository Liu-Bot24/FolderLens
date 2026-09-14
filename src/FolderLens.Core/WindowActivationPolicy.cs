namespace FolderLens.Core;

public static class WindowActivationPolicy
{
    // A delayed launch/open request must not take focus after the user has switched windows.
    public static bool CanActivate(long requestedForeground,long currentForeground,long targetWindow)=>
        currentForeground!=0&&(currentForeground==targetWindow||currentForeground==requestedForeground);
}
