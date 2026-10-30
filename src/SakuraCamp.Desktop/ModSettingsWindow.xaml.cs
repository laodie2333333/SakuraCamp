using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

internal sealed record ModSettingChoice(string Label, LuaValue? Value, string Hover = "");
internal sealed class ModSettingRow : INotifyPropertyChanged
{
    public required ModOption Option { get; init; }
    public string Name => Option.Name;
    public string Label => Option.Label;
    public string Hover => Option.Hover;
    public List<ModSettingChoice> Choices { get; } = [];
    public Visibility EditorVisibility => Option.IsHeading ? Visibility.Collapsed : Visibility.Visible;
    public ModSettingChoice? Original { get; set; }
    private ModSettingChoice? selected;
    public ModSettingChoice? Selected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
    public bool Changed => Selected != Original;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class ModSettingsWindow : UserControl
{
    private readonly InstalledMod mod;
    private readonly WorldModSnapshot snapshot;
    private readonly Func<IReadOnlyDictionary<string, LuaValue>, string[], Task> save;
    private List<ModSettingRow> rows = [];
    private bool saving;
    public event Action? Dismissed;
    internal bool Saved { get; private set; }
    public bool IsSaving => saving;
    private string[] Shards => snapshot.Files.Select(f => f.Shard).ToArray();

    public ModSettingsWindow(InstalledMod mod, WorldModSnapshot snapshot, Func<IReadOnlyDictionary<string, LuaValue>, string[], Task> save)
    {
        this.mod = mod; this.snapshot = snapshot; this.save = save;
        InitializeComponent();
        ModTitle.Text = mod.Name;
        ModSubtitle.Text = $"版本 {mod.Version} · {mod.Author} · {mod.Key}";
        BuildRows();
        SaveHint.Text = mod.Options.Any(o => !o.IsHeading) ? "保存前自动备份配置。取消不会写入任何设置。" : "此 Mod 没有提供可配置选项。";
        SaveButton.IsEnabled = mod.Options.Any(o => !o.IsHeading);
    }

    private void BuildRows()
    {
        var found = new List<ModSettingRow>();
            foreach (var option in mod.Options)
            {
                var row = new ModSettingRow { Option = option };
                row.Choices.AddRange(option.Choices.Select(c => new ModSettingChoice(c.Label, c.Value, c.Hover)));
                var current = snapshot.Files.Where(f => Shards.Contains(f.Shard)).Select(f => f.Entry(mod.Key).Options.GetValueOrDefault(option.Name) ?? option.Default).Distinct().ToArray();
                if (current.Length > 1) row.Choices.Insert(0, new("保持各世界原值（不同）", null));
                else if (current.Length == 1 && !row.Choices.Any(c => c.Value == current[0]))
                    row.Choices.Insert(0, new("保留现有值：" + current[0].Code, current[0]));
                row.Selected = current.Length > 1 ? row.Choices[0] : row.Choices.FirstOrDefault(c => c.Value == current.FirstOrDefault());
                row.Original = row.Selected; found.Add(row);
            }
        rows = found; OptionRows.ItemsSource = rows;
    }
    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in rows.Where(r => !r.Option.IsHeading))
        {
            var choice = row.Choices.FirstOrDefault(c => c.Value == row.Option.Default);
            if (choice is not null) row.Selected = choice;
        }
        SaveHint.Text = "已选择全部配置项的默认值；点击保存后生效。";
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        saving = true; SaveButton.IsEnabled = CancelButton.IsEnabled = DefaultsButton.IsEnabled = OptionRows.IsEnabled = false;
        try
        {
            var changes = rows.Where(r => !r.Option.IsHeading && r.Changed && r.Selected?.Value is not null)
                .GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last().Selected!.Value!, StringComparer.Ordinal);
            await save(changes, Shards);
            saving = false; Saved = true; Dismissed?.Invoke();
        }
        catch (Exception ex) { SaveHint.Text = ex.Message; }
        finally { saving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = DefaultsButton.IsEnabled = OptionRows.IsEnabled = true; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Dismiss();
    public void Dismiss() { if (!saving) Dismissed?.Invoke(); }

    internal void CheckSmoke()
    {
        if (rows.Count != mod.Options.Count || !rows.Any(r => !r.Option.IsHeading)) throw new InvalidOperationException("Mod configuration rows missing");
        var row = rows.First(r => !r.Option.IsHeading && r.Choices.Count > 1);
        var original = row.Selected;
        row.Selected = row.Choices.First(c => c != original);
        if (!row.Changed) throw new InvalidOperationException("Mod configuration selection did not change");
        row.Selected = original;
    }

    internal async Task SaveSmokeAsync()
    {
        var row = rows.First(r => !r.Option.IsHeading && r.Choices.Count > 1);
        var original = row.Selected;
        row.Selected = row.Choices.First(c => c != original && c.Value is not null);
        SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (saving && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        if (saving || !Saved) throw new InvalidOperationException("Mod settings save did not complete: " + SaveHint.Text);
    }
}
