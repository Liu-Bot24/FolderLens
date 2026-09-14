using System.IO.Pipes;
using FolderLens.Infrastructure;

if(args.Length==5&&args[0]=="serve")return await RetainedMemoryFixture.Run(args);
if(args.Length!=4||args[0]!="serve")return 2;
string mode=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"mode.txt"));
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"started.txt"),Environment.ProcessId.ToString());
using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(30));
if(mode=="no-connect"){await Task.Delay(Timeout.Infinite,lifetime.Token);return 0;}
using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous);
await pipe.ConnectAsync(lifetime.Token);
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"connected.txt"),"");
if(mode=="no-hello"){await Task.Delay(Timeout.Infinite,lifetime.Token);return 0;}
await ScanWorkerProtocol.Write(pipe,new("hello",args[2],args[3]),lifetime.Token);
var request=await ScanWorkerProtocol.Read(pipe,lifetime.Token);
File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"requested.txt"),request.Type);
await Task.Delay(Timeout.Infinite,lifetime.Token);
return 0;
