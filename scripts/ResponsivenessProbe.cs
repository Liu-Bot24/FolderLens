using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Runs in the supervisor process, never on a FolderLens dispatcher or worker.
public sealed class FolderLensResponsivenessProbe : IDisposable
{
    public sealed class Sample { public string Phase; public double GapMs,ReadMs; }
    private readonly List<Sample> samples=new List<Sample>();
    private readonly Thread thread;
    private readonly ManualResetEventSlim stop=new ManualResetEventSlim();
    private readonly ManualResetEventSlim ready=new ManualResetEventSlim();
    private string phase="baseline";
    private Exception failure;
    public FolderLensResponsivenessProbe(string path)
    {
        thread=new Thread(()=>Run(path)){IsBackground=true,Name="External responsiveness probe"};
        thread.Start();ready.Wait();if(failure!=null)throw failure;
    }
    public void Mark(string value){Volatile.Write(ref phase,value);}
    private void Run(string path)
    {
        try
        {
            using(var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.RandomAccess))
            {
                byte[] bytes=new byte[65536];var clock=Stopwatch.StartNew();double last=clock.Elapsed.TotalMilliseconds;
                ready.Set();
                while(!stop.Wait(10))
                {
                    double now=clock.Elapsed.TotalMilliseconds;string current=Volatile.Read(ref phase);
                    input.Position=0;double readStart=clock.Elapsed.TotalMilliseconds;input.Read(bytes,0,bytes.Length);
                    samples.Add(new Sample{Phase=current,GapMs=now-last,ReadMs=clock.Elapsed.TotalMilliseconds-readStart});last=now;
                }
            }
        }
        catch(Exception error){failure=error;ready.Set();}
    }
    public Sample[] Finish(){stop.Set();thread.Join();if(failure!=null)throw failure;return samples.ToArray();}
    public void Dispose(){stop.Set();thread.Join();stop.Dispose();ready.Dispose();}
}
