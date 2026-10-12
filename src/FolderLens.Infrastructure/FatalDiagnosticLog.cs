using System.Text.Json;
using FolderLens.Core;

namespace FolderLens.Infrastructure;

public static class FatalDiagnosticLog
{
    public static void Record(string? directory,Exception error,string? build)
    {
        if(!DevelopmentDiagnostics.Enabled||directory is null)return;
        // Preserve the normal unhandled-exception behavior; this is only a
        // bounded development record, never a dump or a copy of source media.
        try
        {
            Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"fatal-latest.json");
            if(File.Exists(path))File.Copy(path,Path.Combine(directory,"fatal-previous.json"),true);
            string detail=error.ToString();
            File.WriteAllText(path,JsonSerializer.Serialize(new{utc=DateTimeOffset.UtcNow,process=Environment.ProcessId,build,error.HResult,exception=detail[..Math.Min(detail.Length,16384)]}));
        }
        catch(Exception logError)
        {Console.Error.WriteLine("Fatal diagnostic unavailable: "+logError.GetType().Name);}
    }
}
