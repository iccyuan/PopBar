using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PopBar.Actions;
using PopBar.Config;

namespace PopBar.UI;

/// <summary>
/// "动作": the bar's actions in order. <see cref="_rows"/> is the flat order as saved; the list shows stand-alone rows
/// and one card per plugin with two or more actions (<see cref="_items"/>, rebuilt from the rows). Drag to reorder:
/// rows and whole groups move in the list, members move inside their group.
/// </summary>
internal partial class ActionsPage : SettingsPage
{
    private readonly ObservableCollection<ActionRow> _rows = new();
    private readonly ObservableCollection<object> _items = new();
    /// <summary>Set while the displayed order is written back into <see cref="_rows"/>, which must not regroup meanwhile.</summary>
    private bool _flattening;

    public ActionsPage()
    {
        InitializeComponent();
        _rows.CollectionChanged += (_, _) => { if (!_flattening) Regroup(); };
        ActionsList.ItemsSource = _items;
    }

    protected override void OnAttach()
    {
        // A plugin added / removed actions (e.g. a new send-to target): show them right away.
        Ctx.Plugins.ActionsSynced += OnPluginActionsSynced;
        Ctx.Owner.Closed += (_, _) => Ctx.Plugins.ActionsSynced -= OnPluginActionsSynced;
    }

    protected override void OnLoad(AppConfig c)
    {
        _rows.Clear();
        foreach (var a in c.Actions) _rows.Add(new ActionRow(a.Clone(), Ctx.Plugins));
    }

    public override void Apply(AppConfig c) => c.Actions = _rows.Select(r => r.Def.Clone()).ToList();

    private void OnPluginActionsSynced() => Dispatcher.InvokeAsync(() =>
    {
        // The plugin already wrote these to config: they are part of the saved state, not unsaved edits.
        bool dirty = IsDirty;
        var stored = Ctx.Store.Current.Actions;
        Quietly(() =>
        {
            if (!dirty)
            {
                _rows.Clear();
                foreach (var a in stored) _rows.Add(new ActionRow(a.Clone(), Ctx.Plugins));
                return;
            }
            // Unsaved edits stay; only bring in what the plugin itself added, removed or renamed.
            var byRef = stored.Where(a => a.IsPlugin && a.Ref != null).ToDictionary(a => a.Ref!, StringComparer.OrdinalIgnoreCase);
            foreach (var r in _rows.Where(r => r.Def.IsPlugin && (r.Def.Ref == null || !byRef.ContainsKey(r.Def.Ref))).ToList()) _rows.Remove(r);
            for (int i = 0; i < stored.Count; i++)
            {
                var a = stored[i];
                if (!a.IsPlugin) continue;
                var row = _rows.FirstOrDefault(r => string.Equals(r.Def.Ref, a.Ref, StringComparison.OrdinalIgnoreCase));
                if (row == null) _rows.Insert(Math.Min(i, _rows.Count), new ActionRow(a.Clone(), Ctx.Plugins));
                else if (row.Def.Label != a.Label || row.Def.Icon != a.Icon) { row.Def.Label = a.Label; row.Def.Icon = a.Icon; row.Refresh(); }
            }
        });
        if (!dirty) MarkSaved();
        RaiseEdited();
    });

    private static string? OwnerOf(ActionRow r) =>
        r.Def.IsPlugin && r.Def.Ref is { } id && id.IndexOf(':') is var i and > 0 ? id[..i] : null;

    /// <summary>
    /// Rebuild the displayed list: a plugin with two or more actions becomes one group card, placed where its first
    /// action is. Members of a group are kept together, so the saved order is normalised on the next save.
    /// </summary>
    private void Regroup()
    {
        _items.Clear();
        var multi = _rows.Select(OwnerOf).Where(o => o != null).GroupBy(o => o!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var groups = new Dictionary<string, ActionGroupRow>();
        foreach (var r in _rows)
        {
            var owner = OwnerOf(r);
            if (owner == null || !multi.Contains(owner)) { _items.Add(r); continue; }
            if (!groups.TryGetValue(owner, out var g))
            {
                var plugin = Ctx.Plugins.Plugins.FirstOrDefault(l => string.Equals(l.Plugin.Id, owner, StringComparison.OrdinalIgnoreCase))?.Plugin;
                groups[owner] = g = new ActionGroupRow { Owner = owner, Name = plugin?.Name ?? owner, Icon = Ctx.Plugins.IconOf(owner) };
                _items.Add(g);
            }
            g.Add(r);
            g.Note = GroupNote(g);
        }
    }

    /// <summary>How the group looks on the bar: merged by the plugin into one button, or one button per action.</summary>
    private string GroupNote(ActionGroupRow g)
    {
        var first = g.Members.FirstOrDefault()?.Def;
        if (first != null && Ctx.Plugins.GroupOf(first) is { } bar) return $"同时出现两个以上时，操作条上合并为一个「{bar.Label}」按钮";
        return "操作条上每个动作各占一个按钮";
    }

    /// <summary>Write the displayed order back into the flat list.</summary>
    private void Flatten()
    {
        _flattening = true;
        try
        {
            _rows.Clear();
            foreach (var it in _items)
            {
                if (it is ActionRow r) _rows.Add(r);
                else if (it is ActionGroupRow g) foreach (var m in g.Members) _rows.Add(m);
            }
        }
        finally { _flattening = false; }
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ActionGroupRow g) g.IsExpanded = !g.IsExpanded;
    }

    // ---- drag to reorder ----

    private object? _dragItem;
    private ActionGroupRow? _dragParent;
    private bool _dragMoved;

    private static void SetDragging(object item, bool on)
    {
        if (item is ActionRow r) r.IsDragging = on;
        else if (item is ActionGroupRow g) g.IsDragging = on;
    }

    private void DragHandle_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext;
        if (item is not (ActionRow or ActionGroupRow)) return;
        _dragItem = item;
        _dragParent = item is ActionRow row ? _items.OfType<ActionGroupRow>().FirstOrDefault(g => g.Members.Contains(row)) : null;
        _dragMoved = false;
        SetDragging(item, true);
        try { DragDrop.DoDragDrop((DependencyObject)sender, new DataObject("PopBarAction", item), DragDropEffects.Move); }
        finally
        {
            SetDragging(item, false);
            _dragItem = null;
            _dragParent = null;
            if (_dragMoved) { Flatten(); RaiseEdited(); }
        }
        e.Handled = true;
    }

    /// <summary>Live reorder: move the dragged item to the slot under the cursor as it moves.</summary>
    private void ActionsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (_dragItem == null) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        var hit = ActionsList.InputHitTest(e.GetPosition(ActionsList)) as DependencyObject;
        if (_dragParent != null)
        {
            // Inside a group: the nearest element bound to a member row.
            var members = _dragParent.Members;
            for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is FrameworkElement { DataContext: ActionRow t } && members.Contains(t))
                {
                    int from = members.IndexOf((ActionRow)_dragItem), to = members.IndexOf(t);
                    if (from != to) { members.Move(from, to); _dragMoved = true; }
                    return;
                }
            }
            return;
        }
        while (hit != null && hit is not ListBoxItem) hit = VisualTreeHelper.GetParent(hit);
        if ((hit as ListBoxItem)?.DataContext is { } target && !ReferenceEquals(target, _dragItem))
        {
            int from = _items.IndexOf(_dragItem), to = _items.IndexOf(target);
            if (from >= 0 && to >= 0) { _items.Move(from, to); _dragMoved = true; }
        }
    }

    private void ActionsList_Drop(object sender, DragEventArgs e)
    {
        if (_dragItem != null) e.Handled = true;
    }

    // ---- edit / add / delete ----

    private static ActionRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as ActionRow;

    private void EditAction_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        if (r.Def.IsPlugin)
        {
            // The plugin owns the action's settings: its own editor when it has one, else its settings page.
            var configure = r.Configure;
            if (configure == null) { Ctx.ShowPlugin(OwnerOf(r)); return; }
            bool changed;
            try { changed = configure(); }
            catch (Exception ex) { Log.Error($"plugin action {r.Def.Ref} configure", ex); return; }
            // The plugin resynced its label / description; rebuild the row once that has landed.
            if (changed) Dispatcher.InvokeAsync(() => { int i = _rows.IndexOf(r); if (i >= 0) _rows[i] = new ActionRow(_rows[i].Def, Ctx.Plugins); }, DispatcherPriority.Background);
            return;
        }
        var dlg = new ActionEditWindow(r.Def.Clone()) { Owner = Ctx.Owner };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _rows[_rows.IndexOf(r)] = new ActionRow(dlg.Result, Ctx.Plugins);
            RaiseEdited();
        }
    }

    private void DeleteAction_Click(object sender, RoutedEventArgs e)
    {
        var r = RowOf(sender); if (r == null) return;
        if (MessageBox.Show(Ctx.Owner, $"删除动作“{r.Label}”？", "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _rows.Remove(r);
        RaiseEdited();
    }

    /// <summary>
    /// An action's own on / off switch saves at once (only that action's flag; order and edits still wait for 保存).
    /// The binding also fires this as rows are realized: nothing to write when the stored flag already matches, and an
    /// action not saved yet has no stored flag - it goes with the rest on 保存.
    /// </summary>
    private void ActionSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (Loading || (sender as FrameworkElement)?.DataContext is not ActionRow row) return;
        var id = row.Def.Id;
        bool on = row.Enabled;
        var stored = Ctx.Store.Current.Actions.FirstOrDefault(a => a.Id == id);
        if (stored == null || stored.Enabled == on) { RaiseEdited(); return; }
        SaveAtOnce(c => { if (c.Actions.FirstOrDefault(a => a.Id == id) is { } a) a.Enabled = on; });
    }

    private void AddAction_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new ActionEditWindow(new ActionDefinition { Label = "新动作", Icon = "star" }) { Owner = Ctx.Owner };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _rows.Add(new ActionRow(dlg.Result, Ctx.Plugins));
            RaiseEdited();
        }
    }

    private void ResetActions_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Ctx.Owner, "恢复默认动作列表？当前自定义动作会被移除。", "PopBar", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _rows.Clear();
        foreach (var a in DefaultActions.Create()) _rows.Add(new ActionRow(a, Ctx.Plugins));
        RaiseEdited();
    }
}
