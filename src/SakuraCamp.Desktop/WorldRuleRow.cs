using System.ComponentModel;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

internal sealed class WorldRuleRow : INotifyPropertyChanged
{
    public WorldRuleDefinition Definition { get; }
    public string Name => Definition.Name;
    public System.Windows.Media.ImageSource? Icon { get; }
    public string Initial => Name.Length > 0 ? Name[..1] : "?";
    public string Shard { get; }
    public IReadOnlyList<WorldRuleChoice> Choices { get; }
    private readonly WorldRuleChoice original;
    private WorldRuleChoice selected;
    public WorldRuleChoice Selected
    {
        get => selected;
        set { if (value is null || selected == value) return; selected = value; Changed(nameof(Selected)); Changed(nameof(ChangeLabel)); Modified?.Invoke(); }
    }
    public bool Dirty => selected != original;
    public string ChangeLabel => Dirty ? "已修改 · 保存后下次开服生效" : original.Value == LuaValue.Nil ? "沿用存档中的设置" : "当前已保存的规则";
    private bool editable;
    public bool Editable { get => editable; set { editable = value; Changed(nameof(Editable)); } }
    public WorldRuleRow(WorldRuleDefinition definition, string shard, LuaValue? current, System.Windows.Media.ImageSource? icon = null)
    {
        Definition = definition; Shard = shard; Icon = icon;
        var choices = definition.Choices.ToList();
        var value = current ?? LuaValue.Nil;
        original = choices.FirstOrDefault(c => c.Value == value) ?? new(current is null ? "沿用存档" : "保留当前值：" + current.Code, value);
        if (!choices.Contains(original)) choices.Insert(0, original);
        Choices = choices; selected = original;
    }
    public event Action? Modified;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
}
