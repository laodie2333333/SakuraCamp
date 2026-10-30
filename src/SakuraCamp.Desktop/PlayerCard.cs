using System.ComponentModel;
using System.Windows.Media;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

internal sealed class PlayerCard : INotifyPropertyChanged
{
    private PlayerStatus player;
    private readonly IReadOnlyDictionary<string, ImageSource> avatars;
    public PlayerCard(PlayerStatus player, IReadOnlyDictionary<string, ImageSource> avatars) { this.player = player; this.avatars = avatars; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(PlayerStatus status)
    {
        if (player == status) return;
        player = status; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public string UserId => player.UserId;
    public string Name => string.IsNullOrEmpty(player.Name) ? "未命名玩家" : player.Name;
    public string Character => player.Character;
    public string CharacterName => Names.GetValueOrDefault(Character, string.IsNullOrEmpty(Character) ? "正在选择角色" : Character);
    public string Location => player.Location;
    public string PingText => player.PingText;
    public string PingDetail => player.PingMilliseconds.HasValue ? "游戏服务器报告的玩家延迟" : "游戏当前仅提供网络质量等级，没有提供该玩家的毫秒延迟";
    public Brush NetworkColor => player.PingMilliseconds is { } ping ? ping < 100 ? Brushes.MediumAquamarine : ping < 250 ? Brushes.Goldenrod : Brushes.Salmon
        : player.NetworkScore switch { 0 => Brushes.MediumAquamarine, 1 => Brushes.Goldenrod, >= 2 => Brushes.Salmon, _ => Brushes.LightSlateGray };
    public ImageSource? Avatar => avatars.GetValueOrDefault(Character) ?? avatars.GetValueOrDefault("mod");
    public string State => player.Ghost ? "幽灵状态" : player.InWorld ? "探索中" : "选角 / 连接中";
    private string Text(VitalStatus? vital) => player.Ghost ? "幽灵" : vital?.Text ?? (player.InWorld ? "不可用" : "等待入场");
    public string Health => Text(player.Health);
    public string Hunger => Text(player.Hunger);
    public string Sanity => Text(player.Sanity);
    public double HealthPercent => player.Health?.Percent ?? 0;
    public double HungerPercent => player.Hunger?.Percent ?? 0;
    public double SanityPercent => player.Sanity?.Percent ?? 0;
    private static readonly Dictionary<string, string> Names = new()
    {
        ["wilson"] = "威尔逊", ["willow"] = "薇洛", ["wolfgang"] = "沃尔夫冈", ["wendy"] = "温蒂",
        ["wx78"] = "WX-78", ["wickerbottom"] = "薇克巴顿", ["woodie"] = "伍迪", ["wes"] = "韦斯",
        ["waxwell"] = "麦斯威尔", ["wathgrithr"] = "薇格弗德", ["webber"] = "韦伯", ["winona"] = "薇诺娜",
        ["warly"] = "沃利", ["wortox"] = "沃拓克斯", ["wormwood"] = "沃姆伍德", ["wurt"] = "沃特",
        ["walter"] = "沃尔特", ["wanda"] = "旺达", ["wonkey"] = "芜猴"
    };
}
