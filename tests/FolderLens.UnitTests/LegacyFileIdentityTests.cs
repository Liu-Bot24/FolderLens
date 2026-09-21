using FolderLens.Infrastructure;
using Xunit;

namespace FolderLens.UnitTests;

public sealed class LegacyFileIdentityTests
{
    [Fact]
    public void LegacyIdentityUsesTheOpenFileAndRejectsReplacementOrUnknownCreation()
    {
        string root=Path.Combine(Path.GetTempPath(),"FolderLens-legacy-identity",Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string source=Path.Combine(root,"source.txt");File.WriteAllText(source,"original");
        long creation=File.GetCreationTimeUtc(source).ToFileTimeUtc();
        using var original=File.OpenHandle(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var identity=FileAllocation.ReadLegacyIdentity(original,creation);
        Assert.NotNull(identity.Identity);Assert.StartsWith("legacy:",identity.Volume);
        Assert.Equal(identity,FileAllocation.ReadLegacyIdentity(original,creation));
        Assert.Null(FileAllocation.ReadLegacyIdentity(original,0).Identity);
        Assert.Null(FileAllocation.ReadLegacyIdentity(original,creation+1).Identity);

        File.Move(source,Path.Combine(root,"retired.txt"));File.WriteAllText(source,"replacement");
        // Even matching timestamps must not make a different object look like the original.
        File.SetCreationTimeUtc(source,DateTime.FromFileTimeUtc(creation));
        using var replacement=File.OpenHandle(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        var current=FileAllocation.ReadLegacyIdentity(replacement,creation);
        Assert.NotNull(current.Identity);Assert.NotEqual(identity.Identity,current.Identity);
        Assert.Equal(identity,FileAllocation.ReadLegacyIdentity(original,creation));
    }
}
