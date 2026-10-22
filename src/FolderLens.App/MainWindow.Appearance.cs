using System.Numerics;
using FolderLens.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace FolderLens.App;

public sealed partial class MainWindow
{
    private AppearancePreferences appearance=new();
    private bool savingAppearance;

    private async Task RestoreAppearance()
    {
        try
        {
            var saved=await settings!.Load<AppearancePreferences>("appearance.json",lifetime.Token);
            ApplyAppearance((saved??new()).Normalize());
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {ShowError(error);}
    }

    private async void ChooseAppearance(object sender,RoutedEventArgs args)
    {
        if(settings is null||savingAppearance||closing)return;
        using var operation=browserWork.Enter();if(operation is null)return;
        var requested=new AppearancePreferences((string)((RadioMenuFlyoutItem)sender).Tag).Normalize();
        savingAppearance=true;NativeAppearance.IsEnabled=SoftAppearance.IsEnabled=GlassAppearance.IsEnabled=false;
        try
        {
            await settings.Save("appearance.json",requested,lifetime.Token);
            if(!closing)ApplyAppearance(requested);
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){ShowError(error);}
        finally
        {
            savingAppearance=false;NativeAppearance.IsEnabled=SoftAppearance.IsEnabled=GlassAppearance.IsEnabled=true;
            UpdateAppearanceMenu();
        }
    }

    private void ApplyAppearance(AppearancePreferences value)
    {
        value=value.Normalize();
        if(appearance!=value)
        {
            string resource=value.Style switch{AppearancePreferences.Soft=>"Soft",AppearancePreferences.Glass=>"Glass",_=>"Native"};
            var palette=new ResourceDictionary{Source=new Uri($"ms-appx:///Themes/{resource}.xaml")};
            // Slot 0 is WinUI's stock controls; slot 1 is the replaceable palette.
            // Keep the visual tree and native control templates intact.
            Application.Current.Resources.MergedDictionaries[1]=palette;
            appearance=value;
            ApplyViewerOverlayAppearance();
            RefreshAppearanceResources(Shell);
            if(capacityWindow?.Content is FrameworkElement capacityRoot)RefreshAppearanceResources(capacityRoot);
            ImageCanvas.Invalidate();
            if(fullScreen)UpdateViewerInformation();
        }
        UpdateAppearanceMenu();
    }

    private static void RefreshAppearanceResources(FrameworkElement root)
    {
        var requested=root.RequestedTheme;
        root.RequestedTheme=root.ActualTheme==ElementTheme.Dark?ElementTheme.Light:ElementTheme.Dark;
        root.RequestedTheme=requested;
    }

    private bool GlassAppearanceActive=>appearance.Style==AppearancePreferences.Glass;

    // Glass overlays float over the photo, so they stay dark in either app theme
    // and fade in from their own edge. Other appearances keep the original panels.
    private void ApplyViewerOverlayAppearance()
    {
        bool glass=GlassAppearanceActive;
        foreach(var (panel,offset) in new[]{(viewerTop,new Vector3(0,-12,0)),(viewerBottom,new Vector3(0,12,0)),(viewerLeft,new Vector3(-12,0,0)),(viewerRight,new Vector3(12,0,0))})
        {
            if(panel is null)continue;
            if(glass){panel.RequestedTheme=ElementTheme.Light;panel.RequestedTheme=ElementTheme.Dark;}
            else panel.RequestedTheme=ElementTheme.Default;
            SetViewerOverlayTransitions(panel,glass?offset:null);
        }
        if(viewerBottom is not null)viewerBottom.HorizontalAlignment=glass?HorizontalAlignment.Center:HorizontalAlignment.Stretch;
    }

    private static void SetViewerOverlayTransitions(Border panel,Vector3? offset)
    {
        var visual=ElementCompositionPreview.GetElementVisual(panel);
        if(offset is not {} from)
        {
            ElementCompositionPreview.SetImplicitShowAnimation(panel,null);
            ElementCompositionPreview.SetImplicitHideAnimation(panel,null);
            // A finished hide animation leaves its end values on the visual.
            visual.Opacity=1;visual.Properties.InsertVector3("Translation",Vector3.Zero);
            return;
        }
        ElementCompositionPreview.SetIsTranslationEnabled(panel,true);
        var compositor=visual.Compositor;
        var easing=compositor.CreateCubicBezierEasingFunction(new Vector2(.2f,.8f),new Vector2(.2f,1f));
        CompositionAnimationGroup Transition(float opacityFrom,float opacityTo,Vector3 translationFrom,Vector3 translationTo,int milliseconds)
        {
            var fade=compositor.CreateScalarKeyFrameAnimation();fade.Target="Opacity";
            fade.InsertKeyFrame(0,opacityFrom);fade.InsertKeyFrame(1,opacityTo,easing);fade.Duration=TimeSpan.FromMilliseconds(milliseconds);
            var slide=compositor.CreateVector3KeyFrameAnimation();slide.Target="Translation";
            slide.InsertKeyFrame(0,translationFrom);slide.InsertKeyFrame(1,translationTo,easing);slide.Duration=TimeSpan.FromMilliseconds(milliseconds);
            var group=compositor.CreateAnimationGroup();group.Add(fade);group.Add(slide);return group;
        }
        ElementCompositionPreview.SetImplicitShowAnimation(panel,Transition(0,1,from,Vector3.Zero,180));
        ElementCompositionPreview.SetImplicitHideAnimation(panel,Transition(1,0,Vector3.Zero,from,120));
    }

    private void UpdateAppearanceMenu()
    {
        NativeAppearance.IsChecked=appearance.Style==AppearancePreferences.Native;
        SoftAppearance.IsChecked=appearance.Style==AppearancePreferences.Soft;
        GlassAppearance.IsChecked=appearance.Style==AppearancePreferences.Glass;
    }
}
