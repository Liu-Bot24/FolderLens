using System.Globalization;

namespace FolderLens.Infrastructure;

internal sealed record SourceObservationSignature(long Bytes,long Modified,long Created,long? Change,string Identity,long Attributes)
{
    internal static SourceObservationSignature Parse(string signature)
    {
        var head=signature.Split(':',5);int last=head.Length==5?head[4].LastIndexOf(':'):-1;
        if(last<0)throw new InvalidDataException("Invalid source observation.");
        long Number(string value)=>long.Parse(value,CultureInfo.InvariantCulture);
        return new(Number(head[0]),Number(head[1]),Number(head[2]),head[3].Length==0?null:Number(head[3]),head[4][..last],Number(head[4][(last+1)..]));
    }
    internal bool CanCompleteWith(SourceObservationSignature full)=>full.Identity.Length>0&&Bytes==full.Bytes&&Modified==full.Modified&&Created==full.Created
        &&(Change is null||Change==full.Change)&&(Identity.Length==0||Identity==full.Identity)&&Attributes==full.Attributes;
    internal bool SameFileAfterHydration(SourceObservationSignature full)=>Identity.Length>0&&Identity==full.Identity
        &&Bytes==full.Bytes&&Modified==full.Modified&&Created==full.Created
        &&(Attributes&~CloudAttributes)==(full.Attributes&~CloudAttributes);
    private const long CloudAttributes=0x1000|0x40000|0x400000|0x80000|0x100000;
}
