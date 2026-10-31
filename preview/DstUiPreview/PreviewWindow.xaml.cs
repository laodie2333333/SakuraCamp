using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SakuraCamp.Core;
using SakuraCamp.Desktop;
namespace SakuraCamp.Preview;
public partial class PreviewWindow : Window
{
    private IReadOnlyList<WorldRuleDefinition> rules = [];
    private Dictionary<string, ImageSource> icons = [];
    private bool demoRunning;
    private int demoGeneration;
    public PreviewWindow()
    {
        InitializeComponent();
        Loaded += async (_,_) =>
        {
            await LoadPictures();
            DemoLogs.Text="[营火] 这里是饥荒风格参考界面。\n[樱花] 地上与洞穴通道：示例在线。\n[世界] 第 12 天，秋。\n[幸存者] Wilson 和 Willow 已围坐在营火旁。\n[说明] 所有操作仅修改本次演示，不连接樱花或启动服务器。";
            var args=Environment.GetCommandLineArgs();
            if(args.Length==3 && args[1]=="--smoke-output")
            {
                try
                {
                    Directory.CreateDirectory(args[2]);
                    for(var i=0;i<Pages.Items.Count;i++)
                    {
                        Pages.SelectedIndex=i;await Task.Delay(150);UpdateLayout();Save(Path.Combine(args[2],$"preview-{i}.png"));
                    }
                    Pages.SelectedIndex=3;Overlay.Visibility=Visibility.Visible;UpdateLayout();Save(Path.Combine(args[2],"preview-mod-config.png"));Overlay.Visibility=Visibility.Collapsed;
                    Width=MinWidth;Height=MinHeight;Pages.SelectedIndex=2;UpdateLayout();Save(Path.Combine(args[2],"preview-minimum.png"));
                    await File.WriteAllTextAsync(Path.Combine(args[2],"report.json"),"{\"Pages\":7,\"SampleOnly\":true,\"NoOfficialSettings\":true,\"NoServerOrApiCalls\":true,\"IntegratedModOverlay\":true}");
                }
                catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(args[2],"error.txt"),ex.ToString());}
                Close();
            }
        };
    }
    private async Task LoadPictures()
    {
        try
        {
            var data=await Task.Run(()=>
            {
                var found=Discovery.Installation();
                var definitions=WorldRuleCatalog.Load(found.Server);
                var art=WorldRuleImages.Load(found.Server,definitions);
                var avatars=CharacterAvatars.Load(found.Server);
                var mods=ModCatalog.Scan(found.Workshop,found.Server).Where(m=>m.Downloaded&&!m.ClientOnly).Take(9)
                    .Select(m=>new { m.Name,Details=$"{m.Author}  /  {m.Version}",Icon=ModImages.Load(m)}).ToArray();
                return(definitions,art,avatars,mods);
            });
            rules=data.definitions;icons=data.art;SetRules("Master");ModCards.ItemsSource=data.mods;
            PlayerCards.ItemsSource=new[] {new { Name="营地里的威尔逊",Details="Wilson · KU_example_1\n地上  /  网络良好",Icon=data.avatars.GetValueOrDefault("wilson") },new { Name="守着火的薇洛",Details="Willow · KU_example_2\n洞穴  /  网络良好",Icon=data.avatars.GetValueOrDefault("willow") }};
        }
        catch
        {
            RuleCards.ItemsSource=new[]{new {Name="雨",Icon=(ImageSource?)null,Choices=new[]{new WorldRuleChoice("默认",LuaValue.Nil),new WorldRuleChoice("无",LuaValue.Nil)}}};
            ModCards.ItemsSource=new[]{new{Name="Global Positions",Details="示例模组 / 仅供风格参考",Icon=(ImageSource?)null},new{Name="营地工具箱",Details="示例模组 / 仅供风格参考",Icon=(ImageSource?)null}};
        }
    }
    private void SetRules(string shard)
    {
        var keys=shard=="Master"?new[]{"specialevent","autumn","winter","spring","summer","day","weather","hounds","deerclops","beefaloheat","krampus","spawnmode"}:new[]{"weather","wormattacks","earthquakes","slurtles","bunnymen","toadstool","spiders","liefs","moon_spider"};
        RuleCards.ItemsSource=keys.Select(key=>rules.FirstOrDefault(r=>r.Key==key)).Where(r=>r is not null).Select(r=>new { r!.Name, Icon=icons.GetValueOrDefault(r.Key), Choices=r.Choices.OrderBy(c=>c.Value != r.Default).Select(c=>new PreviewRuleChoice(c.Label)).ToArray() }).ToArray();
        RuleLocation.Text=shard=="Master"?"—  主世界  —":"—  洞穴  —";
    }
    private void Master_Click(object sender,RoutedEventArgs e)=>SetRules("Master");
    private void Caves_Click(object sender,RoutedEventArgs e)=>SetRules("Caves");
    private void ToggleMod_Click(object sender,RoutedEventArgs e) { var button=(Button)sender;button.Content=button.Content as string=="启用"?"已启用 ✓":"启用";Notice.Text="已切换示例开关，真实模组和存档保持不变。"; }
    private void Gear_Click(object sender,RoutedEventArgs e)=>Overlay.Visibility=Visibility.Visible;
    private void Dismiss_Click(object sender,RoutedEventArgs e)=>Overlay.Visibility=Visibility.Collapsed;
    private void SelectWorld_Click(object sender,RoutedEventArgs e){Notice.Text="已选择示例世界，可以前往联机设置查看开服演示。";Pages.SelectedIndex=1;}
    private async void DemoStart_Click(object sender,RoutedEventArgs e)
    {
        if(demoRunning)return;demoRunning=true;var generation=++demoGeneration;DemoStart.IsEnabled=false;
        foreach(var phase in new[]{"演示：整理营地行囊…","演示：点亮地上与洞穴的路灯…","演示：等待幸存者到来…"})
        { if(generation!=demoGeneration)return;Notice.Text=phase;DemoLogs.AppendText("\n"+phase);await Task.Delay(650); }
        if(generation==demoGeneration){Notice.Text="演示营火已点亮。没有启动真实服务器，可自由切换页面。";DemoStart.Content="营火已点亮 · 示例";}
    }
    private void DemoStop_Click(object sender,RoutedEventArgs e){demoGeneration++;demoRunning=false;DemoStart.IsEnabled=true;DemoStart.Content="点亮营火 · 开服演示";Notice.Text="演示已结束。";}
    private void CopyDemo_Click(object sender,RoutedEventArgs e)=>Notice.Text="这是界面参考，未复制真实连接信息，也没有打开任何目录。";
    private void Minimize_Click(object sender,RoutedEventArgs e)=>SystemCommands.MinimizeWindow(this);
    private void Maximize_Click(object sender,RoutedEventArgs e){if(WindowState==WindowState.Maximized)SystemCommands.RestoreWindow(this);else SystemCommands.MaximizeWindow(this);}
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Save(string path)
    {
        var image=new RenderTargetBitmap((int)ActualWidth,(int)ActualHeight,96,96,PixelFormats.Pbgra32);image.Render(this);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);encoder.Save(output);
    }
}
public sealed record PreviewRuleChoice(string Label);
public sealed class RoughFrame : Border
{
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);if(ActualWidth<10||ActualHeight<10)return;
        var w=ActualWidth;var h=ActualHeight;
        var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(new Point(2,4),false,true);c.PolyLineTo(new[]{new Point(w*.4,1),new Point(w-3,3),new Point(w-1,h*.6),new Point(w-4,h-2),new Point(w*.55,h-1),new Point(2,h-4),new Point(1,h*.3)},true,false);}g.Freeze();
        dc.DrawGeometry(null,new Pen(new SolidColorBrush(Color.FromArgb(160,11,12,9)),3),g);
        var pen=new Pen(new SolidColorBrush(Color.FromArgb(115,185,161,101)),.7);
        dc.DrawLine(pen,new Point(7,6),new Point(w-8,5));dc.DrawLine(pen,new Point(6,h-6),new Point(w-9,h-5));
    }
}
public sealed class PaperGrain : FrameworkElement
{
    private readonly DrawingBrush grain;
    public PaperGrain()
    {
        var group=new DrawingGroup();var random=new Random(63);
        using(var dc=group.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(25,25,21)),null,new Rect(0,0,160,160));
            for(var i=0;i<200;i++){var x=random.Next(160);var y=random.Next(160);dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)random.Next(3,12),200,180,130)),1),new Point(x,y),new Point(x+random.Next(2,12),y+random.Next(-2,3)));}
        }
        group.Freeze();grain=new DrawingBrush(group){Viewport=new Rect(0,0,160,160),ViewportUnits=BrushMappingMode.Absolute,TileMode=TileMode.Tile};grain.Freeze();
    }
    protected override void OnRender(DrawingContext dc)=>dc.DrawRectangle(grain,null,new Rect(RenderSize));
}
public sealed class WildernessScene : FrameworkElement
{
    public bool IsCave {get;set;}
    protected override void OnRender(DrawingContext dc)
    {
        var w=ActualWidth;var h=ActualHeight;dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(21,24,20)),null,new Rect(RenderSize));
        var pen=new Pen(new SolidColorBrush(Color.FromRgb(109,112,84)),1.3);
        for(var i=0;i<11;i++)
        {
            var x=12+i*w/11;var y=h*.72+(i%3)*8;var height=40+(i*17)%43;
            if(IsCave){var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(new Point(x,0),true,true);c.LineTo(new Point(x+14,height),true,false);c.LineTo(new Point(x+26,0),true,false);}dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(43,48,37)),pen,g);}
            else {dc.DrawLine(pen,new Point(x,y),new Point(x,y-height));for(var b=0;b<4;b++)dc.DrawLine(pen,new Point(x-15+b*2,y-16-b*12),new Point(x+15-b*2,y-16-b*12));}
        }
        var center=new Point(w*.5,h*.78);dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(24,208,153,72)),null,center,42,24);
        var flame=new StreamGeometry();using(var c=flame.Open()){c.BeginFigure(new Point(center.X-10,center.Y+3),true,true);c.BezierTo(new Point(center.X-16,center.Y-8),new Point(center.X+4,center.Y-28),new Point(center.X+2,center.Y-37),true,false);c.BezierTo(new Point(center.X+23,center.Y-10),new Point(center.X+17,center.Y+5),new Point(center.X-10,center.Y+3),true,false);}dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(202,163,80)),null,flame);
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(145,124,80)),4),new Point(center.X-15,center.Y+8),new Point(center.X+15,center.Y+2));
    }
}
static class Program
{
    [STAThread]static void Main(){var app=new Application();app.Run(new PreviewWindow());}
}
