using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FolderLens.App;

internal sealed class DirectoryRuleEditor
{
    internal StackPanel View { get; }=new(){Spacing=10};
    private readonly List<DirectoryRule> rules;
    private readonly ListView list=new(){MaxHeight=180};
    private readonly TextBlock feedback=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ComboBox action=Options("操作",("排除","exclude"),("仅查看","include"),("例外保留","keep"));
    private readonly ComboBox target=Options("匹配内容",("文件夹名称","name"),("相对路径","path"));
    private readonly ComboBox match=Options("匹配方式",("完全相同","equals"),("包含","contains"),("开头是","startsWith"),("通配符","wildcard"),("正则表达式（高级）","regex"));
    private readonly TextBox pattern=new(){Header="名称或规则",PlaceholderText="例如：仅预览"};
    private readonly CheckBox children=new(){Content="包括匹配文件夹的子文件夹",IsChecked=true};
    private int editingIndex=-1;
    private readonly Button add=new(){Content="添加规则"};
    private readonly Button cancelEdit=new(){Content="取消编辑",Visibility=Visibility.Collapsed};
    internal DirectoryRuleEditor(DirectoryRule[] initial,Func<Task<string?>> pick,Func<DirectoryRule[],CancellationToken,Task<DirectoryRulePreview>> preview,CancellationToken cancellation)
    {
        rules=initial.Distinct().ToList();
        View.Children.Add(new TextBlock{Text="按文件夹名称或相对路径筛选，不区分大小写，不检查文件名。例外保留优先于排除。规则只改变显示，不需要重新扫描。",TextWrapping=TextWrapping.Wrap});
        View.Children.Add(list);
        var remove=new Button{Content="删除选中规则",IsEnabled=false};
        var edit=new Button{Content="编辑选中规则",IsEnabled=false};
        list.SelectionChanged+=(_,_)=>{remove.IsEnabled=list.SelectedIndex>=0;edit.IsEnabled=list.SelectedIndex>=0;};
        remove.Click+=(_,_)=>{if(list.SelectedIndex is var i&&i>=0){rules.RemoveAt(i);EndEdit();Refresh();}};View.Children.Add(remove);View.Children.Add(edit);
        edit.Click+=(_,_)=>
        {
            if(list.SelectedIndex<0)return;
            editingIndex=list.SelectedIndex;var rule=rules[editingIndex];
            Select(action,rule.Action);Select(target,rule.Target);Select(match,rule.Match);pattern.Text=rule.Pattern;children.IsChecked=rule.IncludeChildren;
            add.Content="保存规则";cancelEdit.Visibility=Visibility.Visible;pattern.Focus(FocusState.Programmatic);
        };
        View.Children.Add(action);View.Children.Add(target);View.Children.Add(match);View.Children.Add(pattern);View.Children.Add(children);
        View.Children.Add(new TextBlock{Text="通配符：* 匹配任意长度，? 匹配一个字符，都不跨越文件夹层级。例如 cache* 匹配 cache 和 cache01。子文件夹由上方勾选项控制。",TextWrapping=TextWrapping.Wrap});
        var browse=new Button{Content="选择文件夹…"};browse.Click+=async(_,_)=>
        {
            try{string? path=await pick();if(path is null||cancellation.IsCancellationRequested)return;target.SelectedIndex=1;match.SelectedIndex=0;pattern.Text=path;}
            catch(Exception ex){feedback.Text=ex.Message;}
        };View.Children.Add(browse);
        add.Click+=(_,_)=>
        {
            try
            {
                var rule=new DirectoryRule(Tag(action),Tag(target),Tag(match),pattern.Text.Trim(),children.IsChecked==true);rule.Validate();
                if(editingIndex>=0&&rules.Where((_,i)=>i!=editingIndex).Any(other=>other.Action==rule.Action&&other.Target==rule.Target&&other.Match==rule.Match&&other.Pattern==rule.Pattern&&other.IncludeChildren==rule.IncludeChildren))throw new ArgumentException("已经存在相同规则，请编辑原规则或取消本次修改。");
                if(editingIndex>=0)rules[editingIndex]=rule with{Enabled=rules[editingIndex].Enabled};
                else
                {
                    if(rules.Count>=64)throw new ArgumentException("最多支持 64 条文件夹规则。");
                    if(!rules.Contains(rule))rules.Add(rule);
                }
                EndEdit();Refresh();
            }
            catch(Exception ex){feedback.Text=ex.Message;}
        };View.Children.Add(add);cancelEdit.Click+=(_,_)=>EndEdit();View.Children.Add(cancelEdit);
        var test=new Button{Content="预览筛选结果"};test.Click+=async(_,_)=>
        {
            test.IsEnabled=false;
            try
            {
                var draft=Read();var result=await preview(draft,cancellation);
                if(!cancellation.IsCancellationRequested&&draft.SequenceEqual(rules))feedback.Text=$"仅计算文件夹规则，按当前已扫描内容：保留 {result.VisibleFiles:N0} 个文件；隐藏 {result.HiddenFiles:N0} 个文件、{result.HiddenDirectories:N0} 个文件夹。\n"+
                    (result.HiddenExamples.Length==0?"没有匹配的隐藏目录。":"隐藏示例（最多 20 个）：\n"+string.Join("\n",result.HiddenExamples));
            }
            catch(OperationCanceledException){}catch(Exception ex){feedback.Text=ex.Message;}
            finally{test.IsEnabled=true;}
        };View.Children.Add(test);View.Children.Add(feedback);Refresh();
    }
    private void Refresh()
    {
        list.Items.Clear();feedback.Text="";
        for(int i=0;i<rules.Count;i++)
        {
            int index=i;var rule=rules[index];
            var label=new TextBlock{Text=$"{(rule.Action=="include"?"仅查看":rule.Action=="keep"?"例外保留":"排除")} · {(rule.Target=="name"?"名称":"路径")} · {rule.Match switch{"equals"=>"完全相同","contains"=>"包含","startsWith"=>"开头是","wildcard"=>"通配符",_=>"正则"}}：{rule.Pattern}",TextWrapping=TextWrapping.Wrap};
            var box=new CheckBox{IsChecked=rule.Enabled,Content=label};
            ToolTipService.SetToolTip(box,$"{rule.Match switch{"equals"=>"完全相同","contains"=>"包含","startsWith"=>"开头是","wildcard"=>"通配符",_=>"正则表达式"}}；{(rule.IncludeChildren?"包括子文件夹":"仅本层文件")}");
            box.Checked+=(_,_)=>{rules[index]=rules[index] with{Enabled=true};list.SelectedIndex=index;feedback.Text="";};
            box.Unchecked+=(_,_)=>{rules[index]=rules[index] with{Enabled=false};list.SelectedIndex=index;feedback.Text="";};list.Items.Add(box);
        }
    }
    internal DirectoryRule[] Read()
    {
        if(editingIndex>=0)throw new ArgumentException("请先保存正在编辑的规则，或取消编辑。");
        if(!string.IsNullOrWhiteSpace(pattern.Text))throw new ArgumentException("还有未添加的文件夹规则，请先点击“添加规则”，或清空输入。");
        _=new DirectoryRuleSet(rules);return rules.ToArray();
    }
    private static string Tag(ComboBox box)=>(string)((ComboBoxItem)box.SelectedItem).Tag;
    private void EndEdit(){editingIndex=-1;pattern.Text="";add.Content="添加规则";cancelEdit.Visibility=Visibility.Collapsed;feedback.Text="";}
    private static void Select(ComboBox box,string tag)=>box.SelectedItem=box.Items.OfType<ComboBoxItem>().First(item=>item.Tag as string==tag);
    private static ComboBox Options(string header,params (string Label,string Tag)[] entries)
    {
        var box=new ComboBox{Header=header,HorizontalAlignment=HorizontalAlignment.Stretch};
        foreach(var entry in entries)box.Items.Add(new ComboBoxItem{Content=entry.Label,Tag=entry.Tag});box.SelectedIndex=0;return box;
    }
}
