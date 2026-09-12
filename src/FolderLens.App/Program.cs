using Microsoft.UI.Xaml;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public partial class LensApplication : Application
{
    private Window? window;
    public LensApplication() {
        var command=Environment.GetCommandLineArgs();
        if(command.Contains("--verify-refresh"))
        {
            if(command.Contains("--verify-dark"))RequestedTheme=ApplicationTheme.Dark;
            else if(command.Contains("--verify-light"))RequestedTheme=ApplicationTheme.Light;
        }
        InitializeComponent();
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            string[] command=Environment.GetCommandLineArgs();string directory=await AppPaths.DataDirectory(command);
            var broker=await SingleInstanceBroker.Acquire(directory,ActivationRequest.Parse(command));if(broker is null){Exit();return;}
            var main=new MainWindow{InstanceBroker=broker,InitialDataDirectory=directory};window=main;
            if(command.Contains("--verify-refresh")){bool wide=command.Contains("--verify-wide");main.AppWindow.MoveAndResize(new(-16000,-16000,wide?3840:1280,wide?2088:900));main.AppWindow.Show(false);}else window.Activate();
        }
        catch(Exception error)
        {
            if(Environment.GetCommandLineArgs().Contains("--verify-refresh"))
            {Console.Error.WriteLine(error);Environment.ExitCode=1;Exit();return;}
            window=new Window{Title="FolderLens",Content=new Microsoft.UI.Xaml.Controls.TextBlock{Text="无法启动 FolderLens："+error.Message,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24)}};window.Closed+=(_,_)=>Exit();window.Activate();
        }
    }
}
