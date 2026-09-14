using FolderLens.Core;

namespace FolderLens.Infrastructure;

internal static class ScanPathProbe
{
    public static ScanDirectoryPacket Read(string path,bool allowCloud=false)
    {
        path=PathRules.ValidateSource(path);
        try
        {
            var attributes=File.GetAttributes(path);
            if(!allowCloud&&FileAllocation.IsDeferred((long)attributes))return new("excluded",[],"DeferredOffline");
            var identity=FileAllocation.InspectMetadata(path,resolveLocation:(attributes&FileAttributes.Directory)!=0);
            if((attributes&FileAttributes.ReparsePoint)!=0&&identity.ReparseTag is {} tag&&!FileAllocation.IsCloudTag(tag))return new("excluded",[],"LinkSkipped");
            SourceFileStamp? stamp=null;
            if((attributes&FileAttributes.Directory)==0){var file=new FileInfo(path);stamp=new(file.Length,file.LastWriteTimeUtc.Ticks);}
            return new("present",[],PhysicalIdentity:identity.PhysicalIdentity,VolumeIdentity:identity.VolumeIdentity,CaseMode:identity.CaseMode,FileStamp:stamp,ResolvedLocation:identity.ResolvedLocation);
        }
        catch(FileNotFoundException){return new("missing",[],"PathNotFound");}
        catch(DirectoryNotFoundException){return new("missing",[],"PathNotFound");}
        catch(UnauthorizedAccessException){return new("inaccessible",[],"AccessDenied");}
        catch(IOException){return new("offline",[],"IoUnavailable");}
    }
}
