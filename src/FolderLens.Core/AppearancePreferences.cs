namespace FolderLens.Core;

public sealed record AppearancePreferences(string Style="native")
{
    public const string Native="native";
    public const string Soft="soft";
    public const string Glass="glass";
    public AppearancePreferences Normalize()=>new(Style is Soft or Glass?Style:Native);
}
