using Microsoft.UI.Xaml;
using FolderLens.Infrastructure;

namespace FolderLens.App;

public partial class LensApplication : Application
{
    private readonly long launchForeground=WindowFocus.Foreground;
    private Window? window;
    private string? diagnosticDirectory;
    public LensApplication() {
        UnhandledException+=(_,error)=>RecordFatal(error.Exception);
        var command=Environment.GetCommandLineArgs();
        if(command.Contains("--verify-refresh"))
        {
            UnhandledException+=(_,error)=>Console.Error.WriteLine(error.Exception.ToString());
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
            diagnosticDirectory=Path.Combine(directory,"logs");
            var broker=await SingleInstanceBroker.Acquire(directory,ActivationRequest.Parse(command) with{RequestedForeground=launchForeground});if(broker is null){Exit();return;}
            var main=new MainWindow{InstanceBroker=broker,InitialDataDirectory=directory};window=main;
            if(command.Contains("--verify-refresh"))
            {
                bool wide=command.Contains("--verify-wide");
                // Moving outside the display does not remove a window from the
                // taskbar. This is not a guarantee of foreground isolation.
                main.AppWindow.IsShownInSwitchers=false;
                main.AppWindow.MoveAndResize(new(-16000,-16000,wide?3840:1280,wide?2088:900));
                main.AppWindow.Show(false);
            }
            else main.ShowAtStartup(launchForeground);
        }
        catch(Exception error)
        {
            RecordFatal(error);
            if(Environment.GetCommandLineArgs().Contains("--verify-refresh"))
            {Console.Error.WriteLine(error);Environment.ExitCode=1;Exit();return;}
            window=new Window{Title="FolderLens",Content=new Microsoft.UI.Xaml.Controls.TextBlock{Text="无法启动 FolderLens："+UserMessages.Error(error),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(24)}};window.Closed+=(_,_)=>Exit();WindowFocus.Show(window,launchForeground);
        }
    }
    private void RecordFatal(Exception error)
    {
        // Two small local records, never full memory dumps or private file copies.
        // Keep the framework's normal failure behavior; do not mark it handled.
        try
        {
            if(diagnosticDirectory is null)return;
            Directory.CreateDirectory(diagnosticDirectory);
            string path=Path.Combine(diagnosticDirectory,"fatal-latest.json");
            if(File.Exists(path))File.Copy(path,Path.Combine(diagnosticDirectory,"fatal-previous.json"),true);
            string detail=error.ToString();
            File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,process=Environment.ProcessId,build=typeof(LensApplication).Assembly.GetName().Version?.ToString(),error.HResult,exception=detail[..Math.Min(detail.Length,16384)]}));
        }
        catch(Exception logError){Console.Error.WriteLine("Fatal diagnostic unavailable: "+logError.GetType().Name);}
    }
}
