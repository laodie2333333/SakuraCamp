using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SakuraCamp.Core;

namespace SakuraCamp.Desktop;

internal sealed class ModCard(InstalledMod mod, WorldModSnapshot? snapshot, ImageSource? image) : INotifyPropertyChanged
{
    public InstalledMod Mod => mod;
    public string Name => mod.Name;
    public string Version => "版本 " + mod.Version;
    public string Author => "作者 · " + mod.Author;
    public string Key => mod.WorkshopId.Length > 0 ? "工坊 " + mod.WorkshopId : mod.Key;
    public string Error => mod.Error;
    public ImageSource? Image => image;
    public string Initial => Name.Length > 0 ? Name[..1] : "M";
    public bool Enabled => snapshot?.AnyEnabled(mod.Key) == true;
    public bool AllEnabled => snapshot?.AllEnabled(mod.Key) == true;
    public string State => !mod.Downloaded ? "文件缺失" : Enabled ? AllEnabled ? "已启用" : "部分世界启用" : "未启用";
    public Brush StateColor => !mod.Downloaded ? Brushes.Salmon : Enabled ? Brushes.MediumAquamarine : Brushes.LightSlateGray;
    public bool EnableOnToggle => !AllEnabled && mod.Downloaded && mod.Error.Length == 0 && !mod.ClientOnly;
    public string ToggleText => !EnableOnToggle && Enabled ? "停用" : Enabled ? "全部启用" : "启用";
    public Brush ToggleBackground => AllEnabled ? new SolidColorBrush(Color.FromRgb(0x3E, 0x49, 0x51)) : new SolidColorBrush(Color.FromRgb(0xF5, 0xA9, 0x62));
    public Brush ToggleForeground => AllEnabled ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x1E, 0x23, 0x30));
    public string ConfigurationText => mod.Options.Count(o => !o.IsHeading) is var n && n > 0 ? $"{n} 个可配置选项" : "没有可配置选项";
    public string ClientRequirement => mod.AllClientsRequire ? "朋友加入时需同步" : "服务器端生效";
    public Visibility ErrorVisibility => Error.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility WorkshopVisibility => mod.Url.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    private bool editable;
    public bool CanToggle => editable && snapshot is not null && (Enabled || mod.Downloaded && mod.Error.Length == 0 && !mod.ClientOnly);
    public bool CanConfigure => editable && snapshot is not null && mod.Downloaded && mod.Error.Length == 0;
    public void SetEditable(bool value) { if (editable == value) return; editable = value; PropertyChanged?.Invoke(this, new(nameof(CanToggle))); PropertyChanged?.Invoke(this, new(nameof(CanConfigure))); }
    public event PropertyChangedEventHandler? PropertyChanged;
}
