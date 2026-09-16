using Markdig.Renderers;
using Markdig.Renderers.Html.Inlines;
using Markdig.Syntax.Inlines;

namespace FolderLens.Content.Worker;

internal sealed class LocalMediaRenderer(IReadOnlySet<string> videos) : LinkInlineRenderer
{
    protected override void Write(HtmlRenderer renderer,LinkInline link)
    {
        if(!link.IsImage||link.Url is not {} url||!url.StartsWith("https://folderlens.local/assets/",StringComparison.Ordinal))
        {base.Write(renderer,link);return;}
        bool video=videos.Contains(url);
        renderer.Write(video?"<video controls preload=\"none\" data-src=\"":"<img decoding=\"async\" data-src=\"");
        renderer.WriteEscapeUrl(url);renderer.Write("\" aria-label=\"");
        bool enabled=renderer.EnableHtmlForInline;renderer.EnableHtmlForInline=false;
        renderer.WriteChildren(link);renderer.EnableHtmlForInline=enabled;
        renderer.Write(video?"\">此视频无法在阅读区播放。</video>":"\" />");
    }
}
