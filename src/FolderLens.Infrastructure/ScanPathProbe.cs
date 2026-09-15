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
            var identity=FileAllocation.InspectMetadata(path,resolveLocation:true);
            if((attributes&FileAttributes.ReparsePoint)!=0&&identity.ReparseTag is {} tag&&!FileAllocation.IsCloudTag(tag))return new("excluded",[],"LinkSkipped");
            SourceFileStamp? stamp=null;ScanEntry? observation=null;
            if((attributes&FileAttributes.Directory)==0)
            {
                var file=new FileInfo(path);stamp=new(file.Length,file.LastWriteTimeUtc.Ticks);
                observation=new(file.Name,false,file.Length,file.LastWriteTimeUtc.Ticks,file.CreationTimeUtc.Ticks,(long)attributes,FileAllocation.IsDeferred((long)attributes)?"placeholder":"local",null,identity.Allocated,identity.PhysicalIdentity,identity.ChangeTime);
            }
            return new("present",[],PhysicalIdentity:identity.PhysicalIdentity,VolumeIdentity:identity.VolumeIdentity,CaseMode:identity.CaseMode,FileStamp:stamp,ResolvedLocation:identity.ResolvedLocation,FileObservation:observation);
        }
        catch(FileNotFoundException){return new("missing",[],"PathNotFound");}
        catch(DirectoryNotFoundException){return new("missing",[],"PathNotFound");}
        catch(UnauthorizedAccessException){return new("inaccessible",[],"AccessDenied");}
        catch(IOException){return new("offline",[],"IoUnavailable");}
    }
}
