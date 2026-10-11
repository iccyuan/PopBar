using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PopBar.Sdk;
using PopBar.Sdk.Ui;

namespace PopBar.Plugins.Builtin;

/// <summary>
/// Developer helpers that appear only when the selection has the right shape: decode (Base64 / URL / Unicode / HTML),
/// JSON, JWT and timestamps, plus an opt-in "编码工具" panel with every conversion and hashes.
/// </summary>
public sealed partial class CodecPlugin : IPopBarPlugin
{
    private IPluginHost _host = null!;

    public string Id => "codec";
    public string Name => "编码工具";
    public string Description => "选中 Base64、URL 编码、JSON、JWT、时间戳时自动出现对应按钮；另有包含各种编码转换和哈希的工具面板。";
    public Version Version => new(0, 1, 0);

    public void Initialize(IPluginHost host) => _host = host;

    public IReadOnlyList<PluginAction> GetActions() => new[]
    {
        new PluginAction
        {
            Id = "decode", Label = "解码", Icon = "code", Description = "Base64 / URL / Unicode / HTML 实体自动识别解码",
            IsApplicable = ctx => TryDecode(ctx.Text.Trim(), out _, out _),
            Execute = (ctx, _) => { ShowDecode(ctx); return Task.CompletedTask; },
        },
        new PluginAction
        {
            Id = "json", Label = "JSON", Icon = "braces", Description = "格式化 / 压缩 JSON",
            IsApplicable = ctx => TryJson(ctx.Text.Trim(), out _),
            Execute = (ctx, _) => { ShowJson(ctx); return Task.CompletedTask; },
        },
        new PluginAction
        {
            Id = "jwt", Label = "JWT", Icon = "key", Description = "解析 JWT 的头部与载荷（不校验签名）",
            IsApplicable = ctx => JwtRegex().IsMatch(ctx.Text.Trim()),
            Execute = (ctx, _) => { ShowJwt(ctx); return Task.CompletedTask; },
        },
        new PluginAction
        {
            Id = "time", Label = "时间戳", Icon = "clock", Description = "Unix 时间戳与日期互转",
            IsApplicable = ctx => TryTime(ctx.Text.Trim(), out _),
            Execute = (ctx, _) => { ShowTime(ctx); return Task.CompletedTask; },
        },
        new PluginAction
        {
            Id = "tools", Label = "编码", Icon = "code", Description = "编码转换与哈希工具面板",
            EnabledByDefault = false,
            IsApplicable = ctx => ctx.Text.Length is > 0 and <= 200_000,
            Execute = (ctx, _) => { ShowTools(ctx); return Task.CompletedTask; },
        },
    };

    // ------------------------------------------------------------------ detection

    [GeneratedRegex(@"^[A-Za-z0-9+/_-]+={0,2}$")] private static partial Regex Base64Regex();
    [GeneratedRegex(@"%[0-9A-Fa-f]{2}")] private static partial Regex UrlEscapeRegex();
    [GeneratedRegex(@"\\u[0-9A-Fa-f]{4}|\\x[0-9A-Fa-f]{2}")] private static partial Regex UnicodeEscapeRegex();
    [GeneratedRegex(@"&(#\d{1,7}|#x[0-9A-Fa-f]{1,6}|[A-Za-z]{2,8});")] private static partial Regex HtmlEntityRegex();
    [GeneratedRegex(@"^eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]*$")] private static partial Regex JwtRegex();
    [GeneratedRegex(@"^\d{4}[-/.]\d{1,2}[-/.]\d{1,2}([ T]\d{1,2}:\d{2}(:\d{2}(\.\d+)?)?)?\s*(Z|[+-]\d{2}:?\d{2})?$")] private static partial Regex DateRegex();

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Most specific decoding first; only accepted when the result is real, readable text.</summary>
    private static bool TryDecode(string s, out string result, out string kind)
    {
        result = ""; kind = "";
        if (s.Length is < 4 or > 50_000) return false;
        if (JwtRegex().IsMatch(s)) return false; // the JWT action handles those
        if (UrlEscapeRegex().IsMatch(s))
        {
            try { result = Uri.UnescapeDataString(s.Replace('+', ' ')); kind = "URL 解码"; return result != s; } catch { }
        }
        if (UnicodeEscapeRegex().IsMatch(s))
        {
            result = UnescapeUnicode(s); kind = "Unicode 反转义"; return result != s;
        }
        if (HtmlEntityRegex().IsMatch(s))
        {
            result = WebUtility.HtmlDecode(s); kind = "HTML 实体解码"; return result != s;
        }
        var compact = s.Replace("\r", "").Replace("\n", "");
        if (compact.Length >= 8 && compact.Length % 4 != 1 && Base64Regex().IsMatch(compact) && TryBase64(compact, out result))
        {
            kind = "Base64 解码"; return true;
        }
        return false;
    }

    private static bool TryBase64(string s, out string text)
    {
        text = "";
        try
        {
            var std = s.Replace('-', '+').Replace('_', '/');
            std = std.PadRight(std.Length + (4 - std.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(std);
            if (bytes.Length == 0) return false;
            text = StrictUtf8.GetString(bytes);
            int printable = text.Count(c => !char.IsControl(c) || c is '\n' or '\r' or '\t');
            return printable >= text.Length * 0.95;
        }
        catch { return false; }
    }

    private static string UnescapeUnicode(string s) => UnicodeEscapeRegex().Replace(s, m =>
        ((char)Convert.ToInt32(m.Value[2..], 16)).ToString());

    private static bool TryJson(string s, out JsonDocument? doc)
    {
        doc = null;
        if (s.Length < 2 || s.Length > 2_000_000) return false;
        if (!((s[0] == '{' && s[^1] == '}') || (s[0] == '[' && s[^1] == ']'))) return false;
        try { doc = JsonDocument.Parse(s, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); return true; }
        catch { return false; }
    }

    private static bool TryTime(string s, out DateTimeOffset t)
    {
        t = default;
        if (s.Length is 10 or 13 && s.All(char.IsAsciiDigit) && long.TryParse(s, out var n))
        {
            var min = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var max = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
            t = s.Length == 10 ? DateTimeOffset.FromUnixTimeSeconds(n) : DateTimeOffset.FromUnixTimeMilliseconds(n);
            return t > min && t < max;
        }
        if (s.Length <= 40 && DateRegex().IsMatch(s))
            return DateTimeOffset.TryParse(s.Replace('/', '-').Replace('.', '-'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t);
        return false;
    }

    // ------------------------------------------------------------------ panels

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private IPanel Panel(SelectionContext ctx, string title, string icon, string text, string status, double width = 460)
    {
        var p = _host.ShowPanel(new PanelOptions { Title = title, Icon = icon, AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Editable = true, Width = width });
        p.SetText(text);
        p.SetStatus(status);
        p.AddButton("复制", t => _host.CopyToClipboard(t), "copy");
        if (ctx.HasSourceWindow) p.AddButton("替换原文", t => _host.PasteText(t, ctx.SourceWindow));
        return p;
    }

    private void ShowDecode(SelectionContext ctx)
    {
        if (!TryDecode(ctx.Text.Trim(), out var result, out var kind)) return;
        Panel(ctx, "解码", "code", result, $"{kind} · {result.Length} 字符");
    }

    private void ShowJson(SelectionContext ctx)
    {
        if (!TryJson(ctx.Text.Trim(), out var doc) || doc == null) return;
        using (doc)
        {
            var pretty = JsonSerializer.Serialize(doc.RootElement, Pretty);
            var compact = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            string shape = doc.RootElement.ValueKind == JsonValueKind.Array ? $"数组 · {doc.RootElement.GetArrayLength()} 项" : $"对象 · {doc.RootElement.EnumerateObject().Count()} 个键";
            var p = Panel(ctx, "JSON", "braces", pretty, shape, 520);
            p.AddButton("压缩", _ => p.SetText(compact));
            p.AddButton("格式化", _ => p.SetText(pretty));
        }
    }

    private void ShowJwt(SelectionContext ctx)
    {
        var parts = ctx.Text.Trim().Split('.');
        var sb = new StringBuilder();
        string status = "签名未校验";
        try
        {
            string Part(string seg)
            {
                TryBase64(seg, out var json);
                using var d = JsonDocument.Parse(json);
                return JsonSerializer.Serialize(d.RootElement, Pretty);
            }
            sb.AppendLine("// 头部").AppendLine(Part(parts[0])).AppendLine().AppendLine("// 载荷").AppendLine(Part(parts[1]));
            TryBase64(parts[1], out var payloadJson);
            using var payload = JsonDocument.Parse(payloadJson);
            var times = new List<string>();
            foreach (var (key, label) in new[] { ("iat", "签发"), ("nbf", "生效"), ("exp", "过期") })
                if (payload.RootElement.TryGetProperty(key, out var v) && v.TryGetInt64(out var secs))
                    times.Add($"{label} {DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            if (payload.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var e))
            {
                var left = DateTimeOffset.FromUnixTimeSeconds(e) - DateTimeOffset.UtcNow;
                times.Add(left < TimeSpan.Zero ? "已过期" : $"还有 {Humanize(left)}");
            }
            if (times.Count > 0) sb.AppendLine().AppendLine("// 时间").AppendLine(string.Join("\n", times));
        }
        catch (Exception ex) { sb.AppendLine("解析失败：" + ex.Message); }
        Panel(ctx, "JWT", "key", sb.ToString().TrimEnd(), status, 520);
    }

    private void ShowTime(SelectionContext ctx)
    {
        if (!TryTime(ctx.Text.Trim(), out var t)) return;
        var local = t.ToLocalTime();
        var ago = DateTimeOffset.Now - t;
        string rel = Math.Abs(ago.TotalSeconds) < 1 ? "现在" : ago > TimeSpan.Zero ? Humanize(ago) + "前" : Humanize(-ago) + "后";
        var text = string.Join("\n",
            $"本地时间   {local:yyyy-MM-dd HH:mm:ss}  {CultureInfo.GetCultureInfo("zh-CN").DateTimeFormat.GetDayName(local.DayOfWeek)}",
            $"UTC        {t.UtcDateTime:yyyy-MM-dd HH:mm:ss}",
            $"ISO 8601   {local:yyyy-MM-ddTHH:mm:sszzz}",
            $"Unix 秒    {t.ToUnixTimeSeconds()}",
            $"Unix 毫秒  {t.ToUnixTimeMilliseconds()}",
            $"距现在     {rel}");
        Panel(ctx, "时间戳", "clock", text, TimeZoneInfo.Local.DisplayName);
    }

    private static string Humanize(TimeSpan d) =>
        d.TotalDays >= 365 ? $"{d.TotalDays / 365:0.#} 年" :
        d.TotalDays >= 1 ? $"{(int)d.TotalDays} 天" :
        d.TotalHours >= 1 ? $"{(int)d.TotalHours} 小时" :
        d.TotalMinutes >= 1 ? $"{(int)d.TotalMinutes} 分钟" : $"{(int)d.TotalSeconds} 秒";

    // ------------------------------------------------------------------ tools panel

    private static readonly (string Group, string Label, Func<string, string> Op)[] Ops =
    {
        ("编码", "Base64 编码", s => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))),
        ("编码", "Base64 解码", s => TryBase64(s.Trim(), out var r) ? r : throw new FormatException("不是有效的 Base64 文本")),
        ("编码", "URL 编码", Uri.EscapeDataString),
        ("编码", "URL 解码", Uri.UnescapeDataString),
        ("编码", "HTML 转义", WebUtility.HtmlEncode),
        ("编码", "HTML 反转义", WebUtility.HtmlDecode),
        ("编码", "Unicode 转义", s => string.Concat(s.Select(c => c < 128 ? c.ToString() : $"\\u{(int)c:x4}"))),
        ("编码", "Unicode 反转义", UnescapeUnicode),
        ("哈希", "MD5", s => Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)))),
        ("哈希", "SHA-1", s => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)))),
        ("哈希", "SHA-256", s => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))),
        ("哈希", "SHA-512", s => Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(s)))),
        ("JSON", "JSON 格式化", s => JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement, Pretty)),
        ("JSON", "JSON 压缩", s => JsonSerializer.Serialize(JsonDocument.Parse(s).RootElement, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })),
    };

    private void ShowTools(SelectionContext ctx)
    {
        var input = ctx.Text;
        var panel = _host.ShowPanel(new PanelOptions
        {
            Title = "编码工具", Icon = "code", AnchorX = ctx.ScreenX, AnchorY = ctx.ScreenY, Width = 540,
            Source = input, ShowTextArea = false, MaxBodyHeight = 520,
        });
        panel.SetStatus("选择一种转换");
        TextBox? output = null;
        panel.AddButton("复制", _ => { if (output != null) _host.CopyToClipboard(output.Text); }, "copy");
        if (ctx.HasSourceWindow) panel.AddButton("替换原文", _ => { if (output != null) _host.PasteText(output.Text, ctx.SourceWindow); });
        panel.SetContent(() =>
        {
            output = new TextBox
            {
                Text = input, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 12),
                FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            };
            var root = new StackPanel { Children = { output } };
            foreach (var group in Ops.GroupBy(o => o.Group))
            {
                root.Children.Add(new TextBlock { Text = group.Key, Opacity = 0.6, FontSize = 12, Margin = new Thickness(2, 0, 0, 6) });
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                foreach (var op in group)
                {
                    var b = Form.Button(op.Label, () =>
                    {
                        try { output.Text = op.Op(input); panel.SetStatus(op.Label); }
                        catch (Exception ex) { panel.SetStatus($"{op.Label} 失败：{ex.Message}"); }
                    });
                    b.Margin = new Thickness(0, 0, 6, 6);
                    wrap.Children.Add(b);
                }
                root.Children.Add(wrap);
            }
            return root;
        });
    }

    public void Dispose() { }
}
