namespace FolderLens.Core;

/// <summary>Compile-time policy. Runtime arguments and settings cannot enable
/// diagnostic collection in a production build.</summary>
public static class DevelopmentDiagnostics
{
    public static bool Enabled { get; } =
#if FOLDERLENS_DEVELOPMENT
        true;
#else
        false;
#endif
}
