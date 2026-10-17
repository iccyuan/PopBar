using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using PopBar.Actions;

namespace PopBar.UI;

/// <summary>Several actions contributed by one plugin, shown as one card so their relation is visible.</summary>
public sealed class ActionGroupRow : INotifyPropertyChanged
{
    public required string Owner { get; init; }
    public required string Name { get; init; }
    public required string Icon { get; init; }
    private string _note = "";
    public string Note { get => _note; set { _note = value; Notify(nameof(Note)); } }
    public ObservableCollection<ActionRow> Members { get; } = new();
    /// <summary>Expanded: how many actions; collapsed: how many of them are on, since the switches are hidden.</summary>
    public string CountText => IsExpanded ? $"{Members.Count} 个动作" : $"已开启 {Members.Count(m => m.Enabled)}/{Members.Count}";

    /// <summary>Groups start folded; the ones the user opened are kept for the app's lifetime (reopening settings keeps them).</summary>
    private static readonly HashSet<string> Expanded = new(StringComparer.OrdinalIgnoreCase);

    public bool IsExpanded
    {
        get => Expanded.Contains(Owner);
        set
        {
            if (value) Expanded.Add(Owner); else Expanded.Remove(Owner);
            Notify(nameof(IsExpanded)); Notify(nameof(ChevronIcon)); Notify(nameof(CountText));
        }
    }
    public string ChevronIcon => IsExpanded ? "chevron-down" : "chevron-right";

    public void Add(ActionRow row)
    {
        Members.Add(row);
        // A member switch flipping changes the collapsed summary.
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ActionRow.Enabled)) Notify(nameof(CountText)); };
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _isDragging;
    public bool IsDragging { get => _isDragging; set { _isDragging = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DragOpacity))); } }
    public double DragOpacity => _isDragging ? 0.45 : 1.0;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Row view-model for the actions list.</summary>
public sealed class ActionRow : INotifyPropertyChanged
{
    public ActionDefinition Def { get; }
    private readonly Plugins.PluginManager _plugins;
    internal ActionRow(ActionDefinition def, Plugins.PluginManager plugins) { Def = def; _plugins = plugins; }

    /// <summary>The plugin action behind a plugin row (for its description and editor), if it is loaded.</summary>
    private Sdk.PluginAction? PluginAction => Def.IsPlugin && Def.Ref != null && _plugins.TryGet(Def.Ref, out _, out var a) ? a : null;
    /// <summary>The editor the plugin offers for this action (<see cref="Sdk.PluginAction.Configure"/>), if any.</summary>
    internal Func<bool>? Configure => PluginAction?.Configure;

    public string Label => string.IsNullOrWhiteSpace(Def.Label) ? "(未命名)" : Def.Label;
    public Geometry IconGeometry => Icons.Get(Def.Icon);
    /// <summary>Plugin actions open their plugin's own editor (or page) instead of the generic one; every row is editable.</summary>
    public Visibility EditVisibility => Visibility.Visible;
    public string EditTip => Def.IsPlugin && Configure == null ? "在插件设置里编辑" : "编辑";
    public bool Enabled
    {
        get => Def.Enabled;
        set { Def.Enabled = value; OnChanged(nameof(Enabled)); }
    }
    private bool _isDragging;
    /// <summary>The row being dragged is dimmed so the drop slot is obvious.</summary>
    public bool IsDragging { get => _isDragging; set { _isDragging = value; OnChanged(nameof(DragOpacity)); } }
    public double DragOpacity => _isDragging ? 0.45 : 1.0;
    public string Summary
    {
        get
        {
            // "Open" covers both links and addresses; say which one this row handles.
            if (PluginAction?.Description is { Length: > 0 } pd) return pd;
            var desc = (Def.Type, Def.Match) switch
            {
                (ActionTypes.Open, MatchKinds.Url) => "在浏览器中打开选中的链接",
                (ActionTypes.Open, MatchKinds.Email) => "用默认邮件程序给选中的地址写邮件",
                (ActionTypes.Url, _) when Uri.TryCreate(Def.Url?.Replace("{text}", "x"), UriKind.Absolute, out var u) && u.Host.Length > 0
                    => "在 " + u.Host + " 中打开选中内容",
                _ => ActionTypes.Describe(Def.Type),
            };
            var detail = Def.Type switch
            {
                ActionTypes.Plugin => Def.Ref,
                ActionTypes.Url when !desc.StartsWith("在 ") => Def.Url,
                ActionTypes.Transform => Def.Op + (string.IsNullOrEmpty(Def.Template) ? "" : "  " + Def.Template),
                ActionTypes.Command => Def.Command + " " + Def.Args,
                _ => null,
            };
            var cond = Def.Type == ActionTypes.Open ? "" : Def.Match switch
            {
                MatchKinds.Url => "  · 仅链接",
                MatchKinds.Email => "  · 仅邮箱",
                MatchKinds.Regex => "  · 正则 " + Def.Regex,
                _ => "",
            };
            return desc + (string.IsNullOrWhiteSpace(detail) ? "" : "  —  " + detail) + cond;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh()
    {
        OnChanged(nameof(Label)); OnChanged(nameof(IconGeometry)); OnChanged(nameof(Summary)); OnChanged(nameof(Enabled));
    }
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
