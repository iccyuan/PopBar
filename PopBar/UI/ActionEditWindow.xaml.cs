using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PopBar.Actions;

namespace PopBar.UI;

public partial class ActionEditWindow : Window
{
    public sealed record IconItem(string Name, Geometry Geometry);

    public ActionDefinition? Result { get; private set; }
    private readonly ActionDefinition _def;

    public ActionEditWindow(ActionDefinition def)
    {
        _def = def;
        InitializeComponent();
        SourceInitialized += (_, _) => Core.Acrylic.Apply(this, Core.BackdropKind.Mica, Core.ThemeManager.IsDark, roundCorners: true);

        IconCombo.ItemsSource = Icons.Names.Select(n => new IconItem(n, Icons.Get(n))).ToList();
        TypeCombo.ItemsSource = ActionTypes.All.Select(t => new KeyValuePair<string, string>(t, ActionTypes.Describe(t))).ToList();
        OpCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new("upper", "全部大写"), new("lower", "全部小写"), new("title", "每个单词首字母大写"),
            new("sentence", "句首大写"), new("trim", "去掉首尾空白"), new("reverse", "反转"), new("none", "不转换（仅套模板）"),
        };
        MatchCombo.ItemsSource = new List<KeyValuePair<string, string>>
        {
            new(MatchKinds.Any, "总是显示"), new(MatchKinds.Url, "选中内容是链接"),
            new(MatchKinds.Email, "选中内容是邮箱"), new(MatchKinds.Regex, "匹配正则"),
        };

        LabelBox.Text = def.Label;
        IconCombo.SelectedItem = ((List<IconItem>)IconCombo.ItemsSource).FirstOrDefault(i => string.Equals(i.Name, def.Icon, StringComparison.OrdinalIgnoreCase))
                                 ?? ((List<IconItem>)IconCombo.ItemsSource).First(i => i.Name == "circle");
        TypeCombo.SelectedValue = ActionTypes.All.Contains(def.Type) ? def.Type : ActionTypes.Url;
        UrlBox.Text = def.Url ?? "";
        OpCombo.SelectedValue = def.Op ?? "upper";
        if (OpCombo.SelectedIndex < 0) OpCombo.SelectedIndex = 0;
        TemplateBox.Text = def.Template ?? "";
        CommandBox.Text = def.Command ?? "";
        ArgsBox.Text = def.Args ?? "";
        MatchCombo.SelectedValue = MatchKinds.All.Contains(def.Match) ? def.Match : MatchKinds.Any;
        RegexBox.Text = def.Regex ?? "";
        UpdatePanels();
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();
    private void MatchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePanels();

    private void UpdatePanels()
    {
        if (UrlPanel == null) return;
        var t = TypeCombo.SelectedValue as string;
        UrlPanel.Visibility = t == ActionTypes.Url ? Visibility.Visible : Visibility.Collapsed;
        TransformPanel.Visibility = t == ActionTypes.Transform ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = t == ActionTypes.Command ? Visibility.Visible : Visibility.Collapsed;
        RegexPanel.Visibility = (MatchCombo.SelectedValue as string) == MatchKinds.Regex ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var t = TypeCombo.SelectedValue as string ?? ActionTypes.Url;
        string? error = null;
        if (string.IsNullOrWhiteSpace(LabelBox.Text)) error = "请填写名称。";
        else if (t == ActionTypes.Url && string.IsNullOrWhiteSpace(UrlBox.Text)) error = "请填写网址模板。";
        else if (t == ActionTypes.Command && string.IsNullOrWhiteSpace(CommandBox.Text)) error = "请填写命令。";
        else if ((MatchCombo.SelectedValue as string) == MatchKinds.Regex)
        {
            try { _ = new System.Text.RegularExpressions.Regex(RegexBox.Text); }
            catch (Exception ex) { error = "正则无效：" + ex.Message; }
        }
        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        _def.Label = LabelBox.Text.Trim();
        _def.Icon = (IconCombo.SelectedItem as IconItem)?.Name ?? "circle";
        _def.Type = t;
        _def.Url = t == ActionTypes.Url ? UrlBox.Text.Trim() : null;
        _def.Op = t == ActionTypes.Transform ? OpCombo.SelectedValue as string : null;
        _def.Template = t == ActionTypes.Transform && !string.IsNullOrWhiteSpace(TemplateBox.Text) ? TemplateBox.Text : null;
        _def.Command = t == ActionTypes.Command ? CommandBox.Text.Trim() : null;
        _def.Args = t == ActionTypes.Command ? ArgsBox.Text : null;
        _def.Match = MatchCombo.SelectedValue as string ?? MatchKinds.Any;
        _def.Regex = _def.Match == MatchKinds.Regex ? RegexBox.Text : null;
        Result = _def;
        DialogResult = true;
    }
}
