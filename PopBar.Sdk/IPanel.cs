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
    void SetText(string text);
    void AppendText(string text);
    /// <summary>Small grey line under the result (e.g. "en → zh-CN · Google").</summary>
    void SetStatus(string status);
    /// <summary>Show / hide the progress indicator.</summary>
    void SetBusy(bool busy);
    /// <summary>Add a footer button; the callback receives the current text.</summary>
    void AddButton(string label, Action<string> onClick, string? icon = null);
    void Close();

    event Action? Closed;
}
