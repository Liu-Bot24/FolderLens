namespace FolderLens.Core;

public static class MetadataDemand
{
    // Names, extensions, RAW category, sizes and filesystem dates need no decoder.
    public static bool ForQuery(FilterSpec filter) =>
        filter.Formats.Length > 0 || filter.Animation != "any" ||
        filter.Ranges.Keys.Any(IsContentField) || filter.AspectRatio is not null ||
        filter.Orientation != "any" || filter.FrameRate is not null ||
        filter.VideoCodecs.Length > 0 || filter.AudioCodecs.Length > 0 ||
        filter.Dates.Any(date => date.Field == "captured") || IsContentField(filter.Sort.Field);
    private static bool IsContentField(string field) => field is
        "captured" or "width" or "height" or "longEdge" or "shortEdge" or
        "pixelCount" or "aspectRatio" or "durationMs" or "frameRate" or "format";
}
