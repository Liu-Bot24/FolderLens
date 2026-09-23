using FolderLens.Core;
using FolderLens.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace FolderLens.App;

[Microsoft.UI.Xaml.Data.Bindable]
public sealed class CapacityDisplayRow(CapacityViewRow row,bool complete=true)
{
    public CapacityViewRow Value {get;}=row;
    public string Label=>Value.Label;
    private bool UnconfirmedEmpty=>!complete&&Value.Files==0;
    public string Size=>UnconfirmedEmpty?"未统计完整":complete?FileRow.FormatBytes(Value.KnownBytes):"≥ "+FileRow.FormatBytes(Value.KnownBytes);
    public string Exact=>UnconfirmedEmpty?"尚无已统计文件，不能判断目录为空":$"{Value.KnownBytes:N0} 字节"+(complete?"":"（已统计部分）");
    public string Files=>UnconfirmedEmpty?"—":(complete?"":"≥ ")+$"{Value.Files:N0} 文件";
    public string Percent=>complete?Value.Fraction?.ToString("P1")??"—":"—";
    public double Bar=>100*(Value.Fraction??0);
    public string Detail=>UnconfirmedEmpty?"扫描未完成，不能判断为空":Value.IsDirectFiles?"当前文件夹中的文件":"双击查看子目录";
}

public sealed partial class CapacityWindow : Window
{
    private readonly CatalogStore catalog;
    private readonly Action<string>? preferScan;
    private readonly string rootId;
    private readonly bool currentObservationsOnly;
    private readonly bool physicalCapacity;
    private readonly string physicalRoot;
    private readonly ExclusionSpec[] capacityExclusions;
    private readonly string? scanWorkerExecutable;
    private CancellationTokenSource? physicalStop;
    private Task physicalWork=Task.CompletedTask;
    private long physicalGeneration;
    private string? physicalFailure;
    private readonly CancellationTokenSource lifetime;
    private CancellationTokenSource operation=new();
    private Task work=Task.CompletedTask;
    private Task browseWork=Task.CompletedTask;
    private Task liveUpdate=Task.CompletedTask;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer liveTimer;
    private DateTimeOffset lastAggregate;
    private Task? shutdown;
    private bool closed;
    private bool hasDisplayedPage;
    public bool IsClosing=>closed;
    private long generation,dataVersion,loadedVersion=-1;
    private CapacityReport? report;
    private string directory="";
    private readonly Dictionary<string,string> selectedPaths=new(StringComparer.Ordinal);
    private readonly ComboBox depthChoice=new(){Width=190};
    private readonly Button parent=new(){Content="↑ 上一级",IsEnabled=false};
    private readonly Button refresh=new(){Content="刷新统计"};
    private readonly Button browseButton=new(){Content="在主窗口浏览",IsEnabled=false};
    private readonly TextBlock currentPath=new(){FontSize=15,TextTrimming=TextTrimming.CharacterEllipsis};
    private readonly TextBlock state=new(){TextWrapping=TextWrapping.Wrap};
    private readonly TextBlock logical=new(){FontSize=25};
    private readonly TextBlock files=new(){FontSize=25};
    private readonly TextBlock count=new(){Text="目录排名",FontSize=16};
    private readonly TextBlock chartCaption=new(){FontSize=16,Text="本层容量分布"};
    private readonly TextBlock chartNote=new(){TextWrapping=TextWrapping.Wrap};
    private readonly ProgressRing busy=new(){Width=20,Height=20,IsActive=false};
    private readonly ListView ranking=new(){SelectionMode=ListViewSelectionMode.Single};
    private readonly ItemsControl shares=new();

    public CapacityWindow(CatalogStore catalog,string rootId,string rootPath,CancellationToken cancellation,Func<string,bool,Task> browse,Action<string>? preferScan=null,bool currentObservationsOnly=false,bool physicalCapacity=false,ExclusionSpec[]? exclusions=null,string? scanWorkerExecutable=null)
    {
        this.currentObservationsOnly=currentObservationsOnly;this.catalog=catalog;this.rootId=rootId;this.preferScan=preferScan;
        this.physicalCapacity=physicalCapacity;physicalRoot=rootPath;capacityExclusions=exclusions??[];this.scanWorkerExecutable=scanWorkerExecutable;
        lifetime=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Title="目录容量 · FolderLens";AppWindow.Resize(new Windows.Graphics.SizeInt32(1180,820));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","FolderLens.ico"));
        var root=new Grid{Padding=new Thickness(24),RowSpacing=18,RequestedTheme=ElementTheme.Default,Style=(Style)Application.Current.Resources["CapacityRootStyle"]};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),GridLength.Auto})root.RowDefinitions.Add(new RowDefinition{Height=height});
        var title=new StackPanel{Spacing=5};title.Children.Add(new TextBlock{Text="目录容量",FontSize=28});
        var rootLabel=new TextBlock{Text=rootPath,TextTrimming=TextTrimming.CharacterEllipsis,Style=(Style)Application.Current.Resources["CapacitySecondaryTextStyle"]};ToolTipService.SetToolTip(rootLabel,rootPath);title.Children.Add(rootLabel);root.Children.Add(title);
        for(int level=1;level<=3;level++)depthChoice.Items.Add(new ComboBoxItem{Content=DepthLabel(level)});
        depthChoice.SelectedIndex=0;
        ToolTipService.SetToolTip(depthChoice,"按目录层级排名；较浅分支显示该分支最深的文件夹");
        ToolTipService.SetToolTip(parent,"返回上一级目录，并恢复之前选中的目录");ToolTipService.SetToolTip(refresh,physicalCapacity?"重新统计当前文件夹及其子目录":"更新当前统计；如需重新查找文件，请在主窗口刷新目录");
        ToolTipService.SetToolTip(browseButton,"查看选中目录的文件；未选中时查看当前目录。保持主窗口原有扫描根和其他筛选。");
        SetCommandContent(parent,"\uE74A","上一级");SetCommandContent(refresh,"\uE72C","刷新统计");SetCommandContent(browseButton,"\uE8B7","在主窗口浏览");
        var toolbar=new Grid{ColumnSpacing=16,RowSpacing=12};for(int i=0;i<2;i++){toolbar.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});toolbar.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});}
        var commands=new StackPanel{Orientation=Orientation.Horizontal,Spacing=12};foreach(var item in new UIElement[]{parent,refresh,browseButton,busy})commands.Children.Add(item);
        toolbar.Children.Add(depthChoice);Grid.SetColumn(commands,1);toolbar.Children.Add(commands);
        var toolbarScroll=new ScrollViewer{Content=toolbar,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(toolbarScroll,1);root.Children.Add(toolbarScroll);
        toolbarScroll.SizeChanged+=(_,_)=>
        {
            bool compact=toolbarScroll.ActualWidth<640;
            Grid.SetColumn(commands,compact?0:1);Grid.SetRow(commands,compact?1:0);
        };
        var overview=new Grid{ColumnSpacing=18};for(int i=0;i<2;i++)overview.ColumnDefinitions.Add(new ColumnDefinition());
        AddMetric(overview,0,"大小",logical,new TextBlock{Text="文件大小合计（含子文件夹）"});
        AddMetric(overview,1,"文件数量",files,new TextBlock{Text="含当前文件夹及子文件夹中的文件"});Grid.SetRow(overview,2);root.Children.Add(overview);
        var body=new Grid{ColumnSpacing=20};body.ColumnDefinitions.Add(new ColumnDefinition());body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(310)});
        body.RowDefinitions.Add(new RowDefinition());body.RowDefinitions.Add(new RowDefinition{Height=new GridLength(0)});
        var table=new Grid{RowSpacing=10};table.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});table.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});table.RowDefinitions.Add(new RowDefinition());
        table.Children.Add(currentPath);Grid.SetRow(count,1);table.Children.Add(count);
        ranking.ItemContainerStyle=(Style)XamlReader.Load("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='HorizontalContentAlignment' Value='Stretch'/></Style>");
        ranking.ItemTemplate=(DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Grid Padding="0,8" ColumnSpacing="10">
                <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="125"/><ColumnDefinition Width="105"/><ColumnDefinition Width="55"/></Grid.ColumnDefinitions>
                <StackPanel Spacing="4"><TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis" ToolTipService.ToolTip="{Binding Value.RelativePath}"/><TextBlock Text="{Binding Detail}" FontSize="11" Foreground="{ThemeResource TextFillColorSecondaryBrush}"/></StackPanel>
                <TextBlock Grid.Column="1" Text="{Binding Size}" ToolTipService.ToolTip="{Binding Exact}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="2" Text="{Binding Files}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
                <TextBlock Grid.Column="3" Text="{Binding Percent}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
              </Grid>
            </DataTemplate>
            """);
        Grid.SetRow(ranking,2);table.Children.Add(ranking);body.Children.Add(table);
        shares.ItemTemplate=(DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <StackPanel Margin="0,0,0,14" Spacing="5">
                <TextBlock Text="{Binding Label}" TextTrimming="CharacterEllipsis" ToolTipService.ToolTip="{Binding Label}"/>
                <ProgressBar Minimum="0" Maximum="100" Value="{Binding Bar}" Height="5"/>
                <Grid><TextBlock Text="{Binding Size}" FontSize="11"/><TextBlock Text="{Binding Percent}" FontSize="11" HorizontalAlignment="Right"/></Grid>
              </StackPanel>
            </DataTemplate>
            """);
        var chart=new StackPanel{Spacing=14};chart.Children.Add(chartCaption);chart.Children.Add(chartNote);chart.Children.Add(shares);
        var chartScroll=new ScrollViewer{Content=chart,Padding=new Thickness(16),Style=(Style)Application.Current.Resources["CapacityChartStyle"]};Grid.SetColumn(chartScroll,1);body.Children.Add(chartScroll);Grid.SetRow(body,3);root.Children.Add(body);
        var pageScroll=new ScrollViewer{Content=root,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalContentAlignment=HorizontalAlignment.Stretch};
        void ArrangePage()
        {
            bool narrow=pageScroll.ActualWidth<808;
            root.Height=narrow?double.NaN:pageScroll.ActualHeight;
            body.ColumnDefinitions[1].Width=new GridLength(narrow?0:310);
            body.RowDefinitions[0].Height=narrow?GridLength.Auto:new GridLength(1,GridUnitType.Star);
            body.RowDefinitions[1].Height=narrow?GridLength.Auto:new GridLength(0);
            ranking.Height=narrow?240:double.NaN;chartScroll.Height=narrow?260:double.NaN;
            body.RowSpacing=narrow?16:0;Grid.SetColumn(chartScroll,narrow?0:1);Grid.SetRow(chartScroll,narrow?1:0);
        }
        pageScroll.SizeChanged+=(_,_)=>ArrangePage();
        Grid.SetRow(state,4);root.Children.Add(state);Content=pageScroll;
        depthChoice.SelectionChanged+=(_,_)=>{SaveSelection();QueueRender(preserve:true);};
        refresh.Click+=(_,_)=>{SaveSelection();if(physicalCapacity)RestartPhysicalScan();else dataVersion++;QueueRender();};
        parent.Click+=(_,_)=>Navigate(Path.GetDirectoryName(directory)??"");
        ranking.DoubleTapped+=(_,_)=>{if(ranking.SelectedItem is CapacityDisplayRow row&&!row.Value.IsDirectFiles)Navigate(row.Value.RelativePath);};
        async Task Browse()
        {
            browseButton.IsEnabled=false;
            var row=(ranking.SelectedItem as CapacityDisplayRow)?.Value;
            try{await browse(row?.RelativePath??directory,row?.IsDirectFiles??false);}
            catch(Exception error){if(!closed)state.Text="无法进入浏览："+UserMessages.Error(error);}
            finally{if(!closed)browseButton.IsEnabled=hasDisplayedPage&&!busy.IsActive;}
        }
        browseButton.Click+=(_,_)=>{if(browseWork.IsCompleted)browseWork=Browse();};
        liveTimer=DispatcherQueue.CreateTimer();liveTimer.Interval=TimeSpan.FromSeconds(5);liveTimer.Tick+=(_,_)=>{if(liveUpdate.IsCompleted)liveUpdate=CheckForUpdates();};
        Closed+=(_,_)=>_=ShutdownAsync();if(physicalCapacity)RestartPhysicalScan();QueueRender();if(!physicalCapacity)liveTimer.Start();
    }
    private static void AddMetric(Grid grid,int column,string label,TextBlock value,TextBlock detail)
    {
        detail.FontSize=12;detail.Style=(Style)Application.Current.Resources["CapacitySecondaryTextStyle"];detail.TextWrapping=TextWrapping.Wrap;
        var panel=new StackPanel{Spacing=7};panel.Children.Add(new TextBlock{Text=label});panel.Children.Add(value);panel.Children.Add(detail);
        var border=new Border{Child=panel,Style=(Style)Application.Current.Resources["CapacityMetricStyle"]};Grid.SetColumn(border,column);grid.Children.Add(border);
    }
    private static void SetCommandContent(Button button,string glyph,string label)
    {
        var panel=new StackPanel{Orientation=Orientation.Horizontal,Spacing=7};
        panel.Children.Add(new FontIcon{Glyph=glyph,FontSize=16});panel.Children.Add(new TextBlock{Text=label});button.Content=panel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,label);
    }
    private static string DepthLabel(int level)=>level switch{1=>"一级子文件夹",2=>"二级子文件夹",3=>"三级子文件夹",_=>$"{level} 级子文件夹"};
    private void EnsureDepthChoices(int maximum)
    {
        for(int level=depthChoice.Items.Count+1;level<=maximum;level++)
            depthChoice.Items.Add(new ComboBoxItem{Content=DepthLabel(level)});
    }
    private void SaveSelection(){if(ranking.SelectedItem is CapacityDisplayRow row)selectedPaths[directory]=row.Value.RelativePath;}
    private void Navigate(string path){SaveSelection();directory=path;if(!physicalCapacity)preferScan?.Invoke(path);QueueRender();}
    private void RestartPhysicalScan()
    {
        physicalStop?.Cancel();var previous=physicalWork;
        var stop=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);physicalStop=stop;
        long current=++physicalGeneration;
        physicalFailure=null;
        report=new CapacityReport(physicalRoot,"scanning",Capacity.BuildDirectories([]));dataVersion++;
        physicalWork=RunPhysicalScan(previous,stop,current);
    }
    private async Task RunPhysicalScan(Task previous,CancellationTokenSource stop,long current)
    {
        try
        {
            try{await previous;}catch(OperationCanceledException){}catch(Exception){}
            stop.Token.ThrowIfCancellationRequested();
            var progress=new Progress<CapacityReport>(next=>
            {
                if(closed||stop.IsCancellationRequested||current!=physicalGeneration)return;
                report=next;dataVersion++;QueueRender(preserve:true);
            });
            var final=await PhysicalCapacityScanner.Scan(physicalRoot,capacityExclusions,progress,stop.Token,scanWorkerExecutable);
            if(!closed&&!stop.IsCancellationRequested&&current==physicalGeneration)
            {report=final;dataVersion++;QueueRender(preserve:true);}
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        catch(Exception error)
        {
            if(!closed&&current==physicalGeneration)
            {physicalFailure=UserMessages.Error(error);report=report is {} previousReport?previousReport with{State="failed"}:new CapacityReport(physicalRoot,"failed",Capacity.BuildDirectories([]));dataVersion++;QueueRender(preserve:true);}
        }
        finally{if(ReferenceEquals(physicalStop,stop))physicalStop=null;stop.Dispose();}
    }
    private async Task CheckForUpdates()
    {
        if(physicalCapacity||closed||!work.IsCompleted||report is null)return;
        try
        {
            var stamp=await new CapacityService(catalog).ChangeStamp(rootId,lifetime.Token);
            if(closed||!work.IsCompleted||report is null)return;
            if(stamp.RootEpoch==report.RootEpoch&&stamp.State==report.State&&(stamp.Revision==report.Revision||DateTimeOffset.UtcNow-lastAggregate<TimeSpan.FromSeconds(15)))return;
            SaveSelection();dataVersion++;QueueRender(preserve:true);await work;
        }
        catch(OperationCanceledException) when(lifetime.IsCancellationRequested){}
        catch(Exception error){if(!closed)state.Text="自动更新统计失败："+UserMessages.Error(error);}
    }
    private void QueueRender(bool preserve=false)
    {
        if(closed)return;long current=++generation;operation.Cancel();var previous=work;
        work=RenderAfter(previous,current,preserve);
    }
    private async Task RenderAfter(Task previous,long current,bool preserve)
    {
        await previous;if(closed||current!=generation)return;
        operation.Dispose();operation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);var token=operation.Token;
        busy.IsActive=true;hasDisplayedPage=false;ranking.IsEnabled=false;browseButton.IsEnabled=false;state.Text="正在统计文件大小…";
        if(!preserve){logical.Text=files.Text="—";ranking.ItemsSource=null;shares.ItemsSource=null;}
        try
        {
            if(report is null||loadedVersion!=dataVersion)
            {
                var service=new CapacityService(catalog);
                var next=physicalCapacity?report??new CapacityReport(physicalRoot,"scanning",Capacity.BuildDirectories([])):await service.EntireRoot(rootId,token,currentObservationsOnly);
                if(closed||current!=generation)return;report=next;loadedVersion=dataVersion;lastAggregate=DateTimeOffset.UtcNow;
            }
            var captured=report;string path=directory;int depth=depthChoice.SelectedIndex+1;
            var page=await Task.Run(()=>
            {
                token.ThrowIfCancellationRequested();var total=captured.Rows.FirstOrDefault(r=>r.RelativePath==path);
                var rows=Capacity.RankAtDepth(captured.Rows,path,depth).Select(r=>new CapacityDisplayRow(r,captured.IsComplete)).ToArray();
                var bars=!captured.IsComplete?[]:Capacity.SharesAtDepth(captured.Rows,path,depth).Select(r=>new CapacityDisplayRow(r)).ToArray();
                int maxDepth=captured.Rows.Where(r=>r.RelativePath!=path&&(path.Length==0||PathRules.IsWithinRelative(r.RelativePath,path)))
                    .Select(r=>1+r.RelativePath[(path.Length==0?0:path.Length+1)..].Count(ch=>ch is '\\' or '/')).DefaultIfEmpty(0).Max();
                token.ThrowIfCancellationRequested();return(total,rows,bars,maxDepth);
            },token);
            if(closed||current!=generation)return;
            EnsureDepthChoices(page.maxDepth);
            currentPath.Text=directory.Length==0?"根目录":directory;ToolTipService.SetToolTip(currentPath,currentPath.Text);parent.IsEnabled=directory.Length>0;
            if(page.total is null)
            {
                logical.Text=files.Text="—";
                ranking.ItemsSource=null;shares.ItemsSource=null;state.Text="该目录已不在当前统计范围。请返回上层查看。";return;
            }
            hasDisplayedPage=true;browseButton.IsEnabled=browseWork.IsCompleted;
            string lowerBound=captured.IsComplete?"":"≥ ";
            logical.Text=lowerBound+FileRow.FormatBytes(page.total?.SubtreeLogical??0);files.Text=lowerBound+(page.total?.SubtreeFiles??0).ToString("N0");
            if(!captured.IsComplete&&page.total!.SubtreeFiles==0)logical.Text=files.Text="未统计完整";
            ToolTipService.SetToolTip(logical,$"{page.total?.SubtreeLogical??0:N0} 字节");
            count.Text=$"{DepthLabel(depth)}排名 · {page.rows.Length:N0} 个目录 · 大小降序"+(page.maxDepth>0&&depth>page.maxDepth?$"（最深 {page.maxDepth} 级）":"");
            ranking.ItemsSource=page.rows;if(selectedPaths.TryGetValue(directory,out string? selected))ranking.SelectedItem=page.rows.FirstOrDefault(r=>r.Value.RelativePath==selected);
            shares.ItemsSource=page.bars;chartCaption.Text="目录容量分布";
            chartNote.Text=page.maxDepth==0?"当前目录没有子文件夹。":"每条分支只列一个文件夹；层数不足时列该分支最深的文件夹。上层直属文件单独计入右侧分布。";
            if(!captured.IsComplete)chartNote.Text="统计尚未完成，暂不计算占比。已统计的容量保留；没有统计到文件不表示目录为空。";
            string status=captured.State switch{"ready"=>"统计完成","scanning"=>"正在统计，当前数字不完整","cancelled"=>"扫描已取消，统计不完整","failed"=>"统计中断，当前数字不完整","offline" or "offlineSnapshot"=>"文件夹暂时无法访问，显示上次统计","notStarted"=>"尚未完成扫描",_=>"部分统计，部分文件夹无法读取"};
            state.Text=$"{status} · 更新于 {captured.CalculatedAt:HH:mm:ss}"+(captured.Pending>0?$" · 待统计 {captured.Pending:N0} 个文件夹":"")+(captured.Unresolvable>0?$" · {captured.Unresolvable:N0} 个文件夹未能完整统计":"")+(captured.State=="failed"&&physicalFailure is {} reason?" · "+reason:"");
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){}
        catch(Exception error){if(!closed&&current==generation)state.Text="统计未完成："+UserMessages.Error(error);}
        finally{if(!closed&&current==generation){busy.IsActive=false;ranking.IsEnabled=true;}}
    }
    public Task ShutdownAsync()=>shutdown??=ShutdownCore();
    private async Task ShutdownCore()
    {
        closed=true;liveTimer.Stop();generation++;physicalStop?.Cancel();lifetime.Cancel();operation.Cancel();await Task.WhenAll(work,browseWork,liveUpdate,physicalWork);
        operation.Dispose();lifetime.Dispose();
    }
}
