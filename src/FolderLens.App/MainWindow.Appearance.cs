using FolderLens.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        savingAppearance=true;NativeAppearance.IsEnabled=SoftAppearance.IsEnabled=false;
        try
        {
            await settings.Save("appearance.json",requested,lifetime.Token);
            if(!closing)ApplyAppearance(requested);
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){ShowError(error);}
        finally
        {
            savingAppearance=false;NativeAppearance.IsEnabled=SoftAppearance.IsEnabled=true;
            UpdateAppearanceMenu();
        }
    }

    private void ApplyAppearance(AppearancePreferences value)
    {
        value=value.Normalize();
        if(appearance!=value)
        {
            string resource=value.Style==AppearancePreferences.Soft?"Soft":"Native";
            var palette=new ResourceDictionary{Source=new Uri($"ms-appx:///Themes/{resource}.xaml")};
            // Slot 0 is WinUI's stock controls; slot 1 is the replaceable palette.
            // Keep the visual tree and native control templates intact.
            Application.Current.Resources.MergedDictionaries[1]=palette;
            appearance=value;
            RefreshAppearanceResources(Shell);
            if(capacityWindow?.Content is FrameworkElement capacityRoot)RefreshAppearanceResources(capacityRoot);
        }
        UpdateAppearanceMenu();
    }

    private static void RefreshAppearanceResources(FrameworkElement root)
    {
        var requested=root.RequestedTheme;
        root.RequestedTheme=root.ActualTheme==ElementTheme.Dark?ElementTheme.Light:ElementTheme.Dark;
        root.RequestedTheme=requested;
    }

    private void UpdateAppearanceMenu()
    {
        NativeAppearance.IsChecked=appearance.Style==AppearancePreferences.Native;
        SoftAppearance.IsChecked=appearance.Style==AppearancePreferences.Soft;
    }
}
