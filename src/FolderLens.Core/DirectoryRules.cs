using System.Text.RegularExpressions;

namespace FolderLens.Core;

public sealed record DirectoryRule(string Action, string Target, string Match, string Pattern, bool IncludeChildren = true, bool Enabled = true)
{
    public void Validate()
    {
        if(Action is not ("include" or "exclude" or "keep") || Target is not ("name" or "path") || Match is not ("equals" or "contains" or "startsWith" or "wildcard" or "regex"))
            throw new ArgumentException("文件夹规则类型无效。");
        if(string.IsNullOrWhiteSpace(Pattern)||Pattern.Length>512||Pattern.Any(char.IsControl))throw new ArgumentException("文件夹规则不能为空，且最多为 512 个字符。");
        if(Match=="regex")
        {
            try { _=CreateRegex(); }
            catch(ArgumentException ex) { throw new ArgumentException("正则表达式无效："+ex.Message,ex); }
            catch(NotSupportedException ex) { throw new ArgumentException("正则不支持回溯引用或环视，请使用普通匹配、分组和重复。",ex); }
        }
        else if(Target=="name"&&(Pattern.Contains('/')||Pattern.Contains('\\')))throw new ArgumentException("文件夹名称不应包含路径分隔符，请改用相对路径。");
        else if(Target=="path"&&(Path.IsPathRooted(Pattern)||Pattern.Contains(':')||Pattern.Split('/','\\').Any(p=>p is "" or "." or "..")))throw new ArgumentException("请填写当前根目录内的相对文件夹路径。");
    }
    internal Regex CreateRegex()
    {
        // Wildcards match a complete name/path. Neither wildcard crosses a path separator;
        // IncludeChildren controls descendant matching independently of the pattern.
        string expression=Match=="wildcard"
            ? @"\A"+Regex.Escape(Pattern.Replace('/','\\')).Replace(@"\*",@"[^\\]*").Replace(@"\?",@"[^\\]")+@"\z"
            : Pattern;
        return new(expression,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking,TimeSpan.FromMilliseconds(50));
    }
}

// Compiled once per query. Called for directories, never for individual files.
public sealed class DirectoryRuleSet
{
    private readonly (DirectoryRule Rule,Regex? Regex)[] rules;
    private readonly bool hasIncludes;
    public DirectoryRuleSet(IEnumerable<DirectoryRule> source)
    {
        var items=source.ToArray();if(items.Length>64)throw new ArgumentException("最多支持 64 条文件夹规则。");
        foreach(var rule in items)rule.Validate();
        rules=items.Where(r=>r.Enabled).Select(r=>(r,r.Match is "regex" or "wildcard"?r.CreateRegex():null)).ToArray();
        hasIncludes=rules.Any(r=>r.Rule.Action=="include");
    }
    public bool IsVisible(string relativeDirectory)
    {
        string path=relativeDirectory.Replace('/','\\');bool included=!hasIncludes,excluded=false,kept=false;
        foreach(var (rule,regex) in rules)
        {
            string candidate=path;
            while(candidate.Length>0)
            {
                string value=rule.Target=="name"?candidate[(candidate.LastIndexOf('\\')+1)..]:candidate;
                string pattern=rule.Match=="regex"?rule.Pattern:rule.Pattern.Replace('/','\\');
                bool match=rule.Match switch
                {
                    "equals"=>value.Equals(pattern,StringComparison.OrdinalIgnoreCase),
                    "contains"=>value.Contains(pattern,StringComparison.OrdinalIgnoreCase),
                    "startsWith"=>value.StartsWith(pattern,StringComparison.OrdinalIgnoreCase),
                    _=>regex!.IsMatch(value)
                };
                if(match)
                {
                    if(rule.Action=="keep")kept=true;else if(rule.Action=="include")included=true;else excluded=true;
                    break;
                }
                if(!rule.IncludeChildren)break;
                int slash=candidate.LastIndexOf('\\');if(slash<0)break;candidate=candidate[..slash];
            }
        }
        return kept||(included&&!excluded);
    }
}
