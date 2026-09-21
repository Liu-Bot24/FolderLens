namespace FolderLens.Core;

public sealed record AppearancePreferences(string Style="native")
{
    public const string Native="native";
    public const string Soft="soft";
    public AppearancePreferences Normalize()=>new(Style==Soft?Soft:Native);
}
