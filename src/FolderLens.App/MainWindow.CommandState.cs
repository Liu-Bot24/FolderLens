using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private bool CanUseViewerAction(ViewerAction action)
    {
        bool file=selected?.Item is not null;
        return action switch
        {
            ViewerAction.Previous or ViewerAction.First=>file&&selected!.Ordinal>0,
            ViewerAction.Next or ViewerAction.Last=>file&&selected!.Ordinal<(results?.Count??firstPageSequence.Length)-1,
            ViewerAction.ReturnBrowser=>immersive||fullScreen,
            ViewerAction.Fit or ViewerAction.Actual or ViewerAction.FitWidth or ViewerAction.FitHeight or ViewerAction.LockSizing or ViewerAction.Rotate or ViewerAction.RotateCounterclockwise=>file&&selected!.Kind=="image"&&fitBitmap is not null&&sourceWidth>0&&sourceHeight>0&&!previewLoading,
            ViewerAction.Slideshow=>slideShow||file&&selected!.Kind=="image"&&!previewLoading,
            _=>file
        };
    }
    private void UpdateCommandAvailability()
    {
        bool file=selected?.Item is not null,folder=!string.IsNullOrEmpty(rootId)&&!replacingRoot;
        foreach(var item in MainMenu.Items.SelectMany(menu=>menu.Items).OfType<MenuFlyoutItem>())
        {
            bool? enabled=(item.Tag as string) switch
            {
                "CopyPath" or "CopyFileReference" or "Reveal" or "ExternalOpen" or "ShowProperties"=>file,
                "RefreshRoot" or "AdvancedFilters" or "FillMetadata" or "SaveView"=>folder,
                "ShowCapacity"=>folder&&activeCollectionId is null,
                "TogglePreview"=>file||immersive,
                "ToggleFullScreen"=>file||fullScreen,
                "ToggleSlideshow"=>CanUseViewerAction(ViewerAction.Slideshow),
                "Fit" or "Actual" or "Rotate" or "RotateCounterclockwise"=>CanUseViewerAction(ViewerAction.Fit),
                _=>null
            };
            if(enabled is {} value)item.IsEnabled=value;
        }
        CapacityButton.IsEnabled=folder&&activeCollectionId is null;
        WindowPreviewButton.IsEnabled=FullScreenPreviewButton.IsEnabled=file;
        BrowseDepthButton.IsEnabled=activeCollectionId is null;
        PreviewFit.IsEnabled=PreviewActual.IsEnabled=PreviewRotate.IsEnabled=CanUseViewerAction(ViewerAction.Fit);
        foreach(var item in viewerActionButtons)item.Button.IsEnabled=CanUseViewerAction(item.Action);
        foreach(var list in new ListViewBase[]{FilesGrid,FilesList})
            if(list.ContextFlyout is MenuFlyout menu)foreach(var item in menu.Items.OfType<MenuFlyoutItem>())item.IsEnabled=file;
    }
}
