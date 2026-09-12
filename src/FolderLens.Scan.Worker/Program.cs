using FolderLens.Infrastructure;

if(args.Length!=4 || args[0]!="serve")return 2;
try{await ScanWorkerProtocol.Serve(args[1],args[2],args[3]);return 0;}
catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
{Console.Error.WriteLine(ex.GetType().Name);return 1;}
