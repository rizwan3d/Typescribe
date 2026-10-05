using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Multi-format clipboard bridge for the canonical Markdown editor. TypeScribe's private rich
/// Markdown flavor is always written first for exact application-to-application round-trips,
/// alongside HTML, RTF and plain Unicode representations for Word, browsers and LibreOffice.
/// </summary>
internal sealed class RichClipboardFeature
{
    private static readonly DataFormat<string> RichMarkdownFormat =
        DataFormat.CreateStringApplicationFormat(RichClipboardCodec.ApplicationFormatId);

    private static readonly DataFormat<byte[]> HtmlFormat =
        DataFormat.CreateBytesPlatformFormat(HtmlPlatformName());

    private static readonly DataFormat<byte[]> RtfFormat =
        DataFormat.CreateBytesPlatformFormat(RtfPlatformName());

    private readonly StudioWorkspaceWindow _window;
    private readonly IDocumentParser _parser = new EmojiDocumentParser(new AdvancedDocumentParser());
    private ManuscriptEditor? _editor;
    private bool _installed;
    private bool _disposed;

    private RichClipboardFeature(StudioWorkspaceWindow window) => _window = window;

    public static void Apply(StudioWorkspaceWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var feature = new RichClipboardFeature(window);
        window.Opened += feature.WindowOpened;
        window.LayoutUpdated += feature.WindowLayoutUpdated;
        window.Closed += feature.WindowClosed;
        feature.TryInstall();
    }

    private void WindowOpened(object? sender, EventArgs e) => TryInstall();
    private void WindowLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void TryInstall()
    {
        if (_installed || _disposed) return;
        var host = _window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(static grid => grid.Classes.Contains("long-form-editor-host"));
        var editor = host?.Children.OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        _editor = editor;
        editor.AddHandler(InputElement.KeyDownEvent, EditorKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _installed = true;
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is null || _disposed) return;

        // Inline RTL paragraphs and inline table cells are native TextBox controls. Let their
        // platform-native clipboard handling own a selection that currently lives inside one of
        // those controls rather than stealing the routed key gesture at the outer manuscript.
        if (e.Source is TextBox) return;

        var command = OperatingSystem.IsMacOS()
            ? e.KeyModifiers.HasFlag(KeyModifiers.Meta)
            : e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (!command) return;

        switch (e.Key)
        {
            case Key.C:
                if (_editor.SelectionLength <= 0) return;
                e.Handled = true;
                _ = CopyAsync(cut: false);
                break;
            case Key.X:
                if (_editor.SelectionLength <= 0 || _editor.IsReadOnly) return;
                e.Handled = true;
                _ = CopyAsync(cut: true);
                break;
            case Key.V:
                if (_editor.IsReadOnly) return;
                e.Handled = true;
                _ = PasteAsync();
                break;
        }
    }

    private async Task CopyAsync(bool cut)
    {
        if (_editor is null || _editor.SelectionLength <= 0) return;
        var clipboard = _window.Clipboard;
        if (clipboard is null) return;

        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        var markdown = _editor.Document.GetText(start, length);
        var exported = RichClipboardCodec.Export(markdown, _parser);

        var item = new DataTransferItem();
        item.Set(RichMarkdownFormat, exported.RichMarkdown);
        item.Set(DataFormat.Text, exported.PlainText);
        item.Set(HtmlFormat, RichClipboardCodec.EncodeHtmlForClipboard(
            exported.Html,
            windowsClipboardHeader: OperatingSystem.IsWindows()));
        item.Set(RtfFormat, exported.Rtf);

        var data = new DataTransfer();
        data.Add(item);
        await clipboard.SetDataAsync(data);

        if (cut && _editor is { IsReadOnly: false })
        {
            _editor.Document.Remove(start, length);
            _editor.CaretOffset = start;
            _editor.Focus();
        }
    }

    private async Task PasteAsync()
    {
        if (_editor is null || _editor.IsReadOnly) return;
        var clipboard = _window.Clipboard;
        if (clipboard is null) return;

        using var data = await clipboard.TryGetDataAsync();
        if (data is null) return;

        var rich = await data.TryGetValueAsync(RichMarkdownFormat);
        byte[]? html = null;
        byte[]? rtf = null;
        string? text = null;

        if (rich is null)
        {
            html = await data.TryGetValueAsync(HtmlFormat);
            if (html is null) rtf = await data.TryGetValueAsync(RtfFormat);
            if (html is null && rtf is null) text = await data.TryGetValueAsync(DataFormat.Text);
        }

        var imported = RichClipboardCodec.Import(rich, html, rtf, text);
        if (imported.Markdown.Length == 0) return;

        var start = _editor.SelectionStart;
        var length = _editor.SelectionLength;
        _editor.Document.Replace(start, length, imported.Markdown);
        _editor.CaretOffset = start + imported.Markdown.Length;
        _editor.Select(_editor.CaretOffset, 0);
        _editor.Focus();
    }

    private static string HtmlPlatformName()
        => OperatingSystem.IsWindows() ? "HTML Format"
            : OperatingSystem.IsMacOS() ? "public.html"
            : "text/html";

    private static string RtfPlatformName()
        => OperatingSystem.IsWindows() ? "Rich Text Format"
            : OperatingSystem.IsMacOS() ? "public.rtf"
            : "text/rtf";

    private void WindowClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        if (_editor is not null)
            _editor.RemoveHandler(InputElement.KeyDownEvent, EditorKeyDown);
        _window.Opened -= WindowOpened;
        _window.LayoutUpdated -= WindowLayoutUpdated;
        _window.Closed -= WindowClosed;
    }
}