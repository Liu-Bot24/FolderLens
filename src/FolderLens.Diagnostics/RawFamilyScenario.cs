using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FolderLens.Contracts;
using FolderLens.Infrastructure;

namespace FolderLens.Diagnostics;

internal static class RawFamilyScenario
{
    public static async Task<int> Run(string directory,string manifestFile,string fixtureRoot,string workerExecutable)
    {
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(manifestFile));var results=new List<object>();bool failed=false;
        await using var worker=new WorkerClient(workerExecutable,Path.Combine(directory,"worker"));long selection=0;
        foreach(var fixture in manifest.RootElement.GetProperty("fixtures").EnumerateArray())
        {
            string id=fixture.GetProperty("fixtureId").GetString()!,expected=fixture.GetProperty("sha256").GetString()!;
            if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-z0-9-]{1,80}$")||!System.Text.RegularExpressions.Regex.IsMatch(expected,"^[a-fA-F0-9]{64}$"))throw new InvalidDataException("Invalid fixture identifier or digest.");
            string path=Path.GetFullPath(Path.Combine(fixtureRoot,fixture.GetProperty("relativePath").GetString()!));
            if(!path.StartsWith(Path.GetFullPath(fixtureRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Fixture escaped its root.");
            string? before=null,after=null;int width=0,height=0;var operations=new List<object>();
            try
            {
                before=await Hash(path);if(!before.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Source checksum mismatch.");
                var stat=new FileInfo(path);var stamp=new SourceFileStamp(stat.Length,stat.LastWriteTimeUtc.Ticks);var context=new RequestContext("raw-family",1,1,++selection,1,1);
                foreach(string operation in new[]{"probe","rawEmbedded","rawDevelop","thumbnail","fit","fullTile"})
                {
                    ImageReply? reply=null;var clock=Stopwatch.StartNew();
                    try
                    {
                        int x=width/2048,y=height/2048;
                        int targetWidth=operation=="thumbnail"?256:1600,targetHeight=operation=="thumbnail"?256:1200;
                        reply=await worker.Request(path,operation,context,new(targetWidth,targetHeight,TileX:x,TileY:y),CancellationToken.None,stamp);
                        var data=reply.Message.Metadata!.Value;int actualWidth=data.GetProperty("width").GetInt32(),actualHeight=data.GetProperty("height").GetInt32();
                        if(operation!="rawEmbedded")
                        {
                            width=actualWidth;height=actualHeight;
                            if((long)width*height<fixture.GetProperty("megapixels").GetDouble()*1_000_000*.8)throw new InvalidDataException("RAW output is smaller than the camera sample's full image.");
                        }
                        if(operation=="rawEmbedded"&&reply.Message.Quality!="rawEmbedded"||operation is "rawDevelop" or "fullTile"&&reply.Message.Quality!="rawDeveloped")throw new InvalidDataException("Incorrect RAW quality label.");
                        int? assetWidth=null,assetHeight=null;string? assetHash=null;
                        if(reply.AssetPath is {} asset)
                        {
                            byte[] header=new byte[24];using(var stream=File.OpenRead(asset))await stream.ReadExactlyAsync(header);
                            assetWidth=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16,4));assetHeight=BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20,4));
                            if(operation=="fullTile"&&(assetWidth!=Math.Min(1024,width-x*1024)||assetHeight!=Math.Min(1024,height-y*1024)))throw new InvalidDataException("Original tile size mismatch.");
                            if(operation!="fullTile"&&(assetWidth>targetWidth||assetHeight>targetHeight||assetWidth<1||assetHeight<1))throw new InvalidDataException("Fit output size mismatch.");
                            string retained=Path.Combine(directory,id+"-"+operation+".png");File.Copy(asset,retained,true);assetHash=await Hash(retained);
                        }
                        operations.Add(new{operation,status="PASS",quality=reply.Message.Quality,sourceWidth=actualWidth,sourceHeight=actualHeight,assetWidth,assetHeight,assetHash,elapsedMs=clock.Elapsed.TotalMilliseconds,metadata=data.Clone()});
                    }
                    catch(Exception ex){failed=true;operations.Add(new{operation,status="FAIL",error=ex.Message,elapsedMs=clock.Elapsed.TotalMilliseconds});}
                    finally{if(reply is not null)await worker.ReleaseAsset(reply);}
                }
                after=await Hash(path);if(before!=after)throw new InvalidDataException("Source changed during viewing.");
            }
            catch(Exception ex){failed=true;operations.Add(new{operation="source-integrity",status="FAIL",error=ex.Message});}
            var result=new{fixture=fixture.Clone(),sourceBefore=before,sourceAfter=after,sourceUnchanged=before is not null&&before==after,operations};results.Add(result);
            await File.WriteAllTextAsync(Path.Combine(directory,id+".json"),JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"{id}: {string.Join(", ",operations.Select(o=>JsonSerializer.Serialize(o).Contains("\"FAIL\"")?"FAIL":"PASS"))}");
        }
        string json=JsonSerializer.Serialize(new{buildId=WorkerProtocol.BuildId,providerIdentity=await worker.GetProviderIdentity(CancellationToken.None),workerSha256=await Hash(workerExecutable),createdUtc=DateTimeOffset.UtcNow,scope="One real sample per RAW family; not independent color, all-camera, UI or performance acceptance",results},new JsonSerializerOptions{WriteIndented=true});
        await File.WriteAllTextAsync(Path.Combine(directory,"raw-family-results.json"),json);return failed?1:0;
    }
    private static async Task<string> Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(await SHA256.HashDataAsync(file));}
}
