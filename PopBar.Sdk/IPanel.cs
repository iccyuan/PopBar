namespace PopBar.Sdk;

/// <summary>A floating result panel created by <see cref="IPluginHost.ShowPanel"/>. All methods marshal to the UI thread.</summary>
public interface IPanel
{
    /// <summary>Cancelled when the user closes the panel; pass it to streaming calls.</summary>
    CancellationToken Cancellation { get; }
    bool IsOpen { get; }
    /// <summary>Current result text (what the user sees / may have edited).</summary>
    string Text { get; }

    void SetTitle(string title);
    /// <summary>Small tag next to the title naming what produced the result, e.g. "Google 翻译" or a model name. Null hides it.</summary>
    void SetBadge(string? badge);
    void SetText(string text);
    void AppendText(string text);
    /// <summary>Small grey line under the result (e.g. "en → zh-CN · Google").</summary>
    void SetStatus(string status);
    /// <summary>Show / hide the progress indicator.</summary>
    void SetBusy(bool busy);
    /// <summary>Add a footer button; the callback receives the current text.</summary>
    void AddButton(string label, Action<string> onClick, string? icon = null);
    /// <summary>Replace the body with any WPF content (lists, images, forms...). Built on the UI thread via the factory.</summary>
    void SetContent(Func<System.Windows.FrameworkElement> factory);
    /// <summary>Pin / unpin (pinned panels ignore outside clicks).</summary>
    bool Pinned { get; set; }
    void Close();

    event Action? Closed;
}
