using FolderLens.Core;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private async Task VerifyMenuAvailability(string source,Dictionary<string,object> report)
    {
        var checks=new List<object>();var failures=new List<string>();
        void Check(bool condition,string name){checks.Add(new{name,passed=condition});if(!condition)failures.Add(name);}
        report["mainMenuTexts"]=MainMenu.Items.SelectMany(menu=>menu.Items).OfType<MenuFlyoutItem>().Select(item=>item.Text).ToArray();
        MenuFlyoutItem MainItem(string command)=>MainMenu.Items.SelectMany(menu=>menu.Items).OfType<MenuFlyoutItem>().Single(item=>Equals(item.Tag,command));
        Check(!MainItem("CopyPath").IsEnabled,"no selection: copy path disabled");
        Check(!MainItem("ShowProperties").IsEnabled,"no selection: properties disabled");
        Check(!MainItem("Fit").IsEnabled,"no image: fit disabled");
        await OpenRoot(source);if(metadataTask is not null)await metadataTask;await RefreshQuery();
        await SelectBrowserOrdinal(results!,0,lifetime.Token);UpdateViewerInformation();BuildViewerContextMenu();
        Check(MainItem("CopyPath").IsEnabled,"selected file: copy enabled");
        Check(MainItem("Fit").IsEnabled,"ready image: fit enabled");
        Check(!viewerContextMenu!.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text.StartsWith("上一张")).IsEnabled,"first file: previous disabled");
        Check(viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text.StartsWith("下一张")).IsEnabled,"first file: next enabled");
        Check(!viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text=="返回浏览列表").IsEnabled,"browser preview: return disabled");
        PreparePreview();BuildViewerContextMenu();
        Check(!MainItem("Fit").IsEnabled,"loading image: main fit disabled");
        Check(!viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text.StartsWith("适应屏幕")).IsEnabled,"loading image: context fit disabled");
        Check(!PreviewFit.IsEnabled&&!PreviewActual.IsEnabled&&!PreviewRotate.IsEnabled,"loading image: side actions disabled");
        FinishPreview();UpdateViewerInformation();
        await SetImmersive(true);BuildViewerContextMenu();
        Check(viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text=="返回浏览列表").IsEnabled,"viewer: return enabled");
        await ReturnToBrowser();
        await SelectBrowserOrdinal(results!,results!.Count-1,lifetime.Token);BuildViewerContextMenu();
        Check(!viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text.StartsWith("下一张")).IsEnabled,"last file: next disabled");
        folderGrouping=new(false);UpdateGroupingButton();
        await ResetFirstPageFixture();
        verifyCandidateBarrier=_=>throw new IOException("Menu first-page fixture: full snapshot unavailable");
        try{await RefreshQuery();}finally{verifyCandidateBarrier=null;}
        if(results is not null||firstPageSequence.Length<2)throw new InvalidOperationException("未形成真实首批结果状态。");
        await SelectPreview(firstPageSequence[0]);BuildViewerContextMenu();
        Check(viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text.StartsWith("下一张")).IsEnabled,"first-page only: next enabled");
        await RunViewerAction(ViewerAction.Last);
        await WaitUntil(()=>!previewLoading,TimeSpan.FromSeconds(5));
        Check(selected?.Ordinal==firstPageSequence.Length-1,"first-page only: last command navigates");
        BuildViewerContextMenu();
        Check(!viewerContextMenu.Items.OfType<MenuFlyoutItem>().Single(item=>item.Text.StartsWith("下一张")).IsEnabled,"first-page only: last boundary disabled");
        report["checks"]=checks;report["failures"]=failures;
        if(failures.Count>0)throw new InvalidOperationException(string.Join("; ",failures));
        report["status"]="PASS";
    }
}
