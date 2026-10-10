using System.Windows;

namespace PopBar.Sdk;

/// <summary>
/// Capabilities plugins share with each other without referencing each other: one plugin provides an
/// implementation of an SDK contract (e.g. <see cref="ITextTranslator"/>), others look it up by type.
/// </summary>
public interface IServiceRegistry
{
    /// <summary>Publish an implementation. A later Provide for the same contract replaces the earlier one.</summary>
    void Provide<T>(T service) where T : class;
    /// <summary>The current implementation, or null when no loaded (enabled) plugin provides it.</summary>
    T? Get<T>() where T : class;
}

/// <summary>Text translation (provided by the built-in translate plugin).</summary>
public interface ITextTranslator
{
    /// <summary>Translate <paramref name="text"/>. Null <paramref name="targetLang"/> = the user's configured target
    /// (with the usual "already in the target language → alternate language" switch). Line breaks are preserved.</summary>
    Task<TranslationResult> TranslateAsync(string text, string? targetLang = null, CancellationToken ct = default);
}

public sealed record TranslationResult(string Text, string SourceLang, string TargetLang, string Engine);

/// <summary>Optical character recognition (provided by the built-in OCR plugin, Windows engine, offline).</summary>
public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(byte[] png, CancellationToken ct = default);
}

/// <summary>Recognized text plus per-line boxes in the source image's pixel coordinates.</summary>
public sealed record OcrResult(string Text, IReadOnlyList<OcrLine> Lines);

public sealed record OcrLine(string Text, Rect Bounds);
