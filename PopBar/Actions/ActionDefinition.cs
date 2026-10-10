namespace PopBar.Actions;

/// <summary>One button on the bar. Serialized to config.json.</summary>
public sealed class ActionDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public bool Enabled { get; set; } = true;
    public string Label { get; set; } = "";
    /// <summary>Icon name from <see cref="UI.Icons"/>.</summary>
    public string Icon { get; set; } = "circle";

    /// <summary>copy | url | open | transform | paste | command</summary>
    public string Type { get; set; } = ActionTypes.Url;

    /// <summary>For url: template with {text} (URL-encoded) or {raw}.</summary>
    public string? Url { get; set; }
    /// <summary>For transform: upper | lower | title | sentence | trim | reverse | none.</summary>
    public string? Op { get; set; }
    /// <summary>For transform: wrap result, e.g. "「{text}」".</summary>
    public string? Template { get; set; }
    /// <summary>For command: executable path.</summary>
    public string? Command { get; set; }
    /// <summary>For command: argument string, {text} is substituted (quotes escaped).</summary>
    public string? Args { get; set; }

    /// <summary>any | url | email | regex — the button only appears when the selection matches.</summary>
    public string Match { get; set; } = MatchKinds.Any;
    public string? Regex { get; set; }

    public ActionDefinition Clone() => (ActionDefinition)MemberwiseClone();
}

public static class ActionTypes
{
    public const string Copy = "copy";
    public const string Url = "url";
    public const string Open = "open";
    public const string Transform = "transform";
    public const string Paste = "paste";
    public const string Command = "command";

    public static readonly string[] All = { Copy, Url, Open, Transform, Paste, Command };

    public static string Describe(string type) => type switch
    {
        Copy => "复制到剪贴板",
        Url => "打开网址（{text} 替换为选中内容）",
        Open => "打开选中的链接 / 邮箱",
        Transform => "转换后替换选中文本",
        Paste => "用剪贴板内容替换选中文本",
        Command => "运行外部命令",
        _ => type,
    };
}

public static class MatchKinds
{
    public const string Any = "any";
    public const string Url = "url";
    public const string Email = "email";
    public const string Regex = "regex";
    public static readonly string[] All = { Any, Url, Email, Regex };
}

public static class TransformOps
{
    public static readonly string[] All = { "upper", "lower", "title", "sentence", "trim", "reverse", "none" };

    public static string Apply(string? op, string text) => (op ?? "none").ToLowerInvariant() switch
    {
        "upper" => text.ToUpperInvariant(),
        "lower" => text.ToLowerInvariant(),
        "title" => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
        "sentence" => Sentence(text),
        "trim" => text.Trim(),
        "reverse" => new string(text.Reverse().ToArray()),
        _ => text,
    };

    private static string Sentence(string s)
    {
        if (s.Length == 0) return s;
        var lower = s.ToLowerInvariant();
        int i = 0;
        while (i < lower.Length && char.IsWhiteSpace(lower[i])) i++;
        if (i >= lower.Length) return lower;
        return lower[..i] + char.ToUpperInvariant(lower[i]) + lower[(i + 1)..];
    }
}

public static class DefaultActions
{
    public static List<ActionDefinition> Create() => new()
    {
        new() { Id = "copy", Label = "复制", Icon = "copy", Type = ActionTypes.Copy },
        new() { Id = "search", Label = "搜索", Icon = "search", Type = ActionTypes.Url, Url = "https://www.google.com/search?q={text}" },
        new() { Id = "translate", Label = "翻译", Icon = "languages", Type = ActionTypes.Url, Url = "https://translate.google.com/?sl=auto&tl=zh-CN&text={text}&op=translate" },
        new() { Id = "open", Label = "打开链接", Icon = "external-link", Type = ActionTypes.Open, Match = MatchKinds.Url },
        new() { Id = "mail", Label = "发邮件", Icon = "mail", Type = ActionTypes.Open, Match = MatchKinds.Email },
        new() { Id = "upper", Label = "大写", Icon = "case-upper", Type = ActionTypes.Transform, Op = "upper" },
        new() { Id = "lower", Label = "小写", Icon = "case-lower", Type = ActionTypes.Transform, Op = "lower" },
        new() { Id = "paste", Label = "粘贴", Icon = "clipboard-paste", Type = ActionTypes.Paste },
    };
}
