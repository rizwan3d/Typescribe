using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Incrementally migrates the live studio from Avalonia TextBox manuscript surfaces
/// to <see cref="ManuscriptEditor"/> without breaking the existing command handlers.
/// The detached TextBox remains a short-lived compatibility proxy until the studio
/// commands are moved directly onto ManuscriptEditor.
/// </summary>
internal sealed class ManuscriptEditorUpgrade
{
    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly List<EditorBridge> _bridges = [];
    private readonly HashSet<TextBox> _proxies = [];
    private readonly DispatcherTimer _syncTimer;
    private ManuscriptEditor? _primaryEditor;
    private ComboBox? _revisionPicker;
    private bool _discoverScheduled;
    private bool _disposed;
    private bool _wasCompositionMode;

    private ManuscriptEditorUpgrade(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _syncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _syncTimer.Tick += (_, _) => SynchronizeBridges();
    }

    public static void Apply(StudioWorkspaceWindow window, WorkspaceViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        var upgrade = new ManuscriptEditorUpgrade(window, viewModel);
        window.Opened += upgrade.OnOpened;
        window.LayoutUpdated += upgrade.OnLayoutUpdated;
        window.Closed += upgrade.OnClosed;
        viewModel.StateChanged += upgrade.OnViewModelStateChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _syncTimer.Start();
        ScheduleDiscover();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e) => ScheduleDiscover();

    private void OnViewModelStateChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(UpdatePrimaryDocumentState, DispatcherPriority.Background);

    private void ScheduleDiscover()
    {
        if (_disposed || _discoverScheduled) return;
        _discoverScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _discoverScheduled = false;
            if (!_disposed) DiscoverAndUpgrade();
        }, DispatcherPriority.Background);
    }

    private void DiscoverAndUpgrade()
    {
        var controls = _window.GetVisualDescendants().OfType<Control>().ToArray();
        WireRevisionPicker(controls.OfType<ComboBox>());

        var candidates = controls.OfType<TextBox>()
            .Where(static box => box.AcceptsReturn && box.AcceptsTab)
            .Where(box => !_proxies.Contains(box))
            .ToArray();

        foreach (var proxy in candidates)
            Upgrade(proxy);

        UpdatePrimaryDocumentState();
    }

    private void Upgrade(TextBox proxy)
    {
        if (proxy.Parent is not Panel parent) return;

        var editor = new ManuscriptEditor
        {
            Margin = proxy.Margin,
            HorizontalAlignment = proxy.HorizontalAlignment,
            VerticalAlignment = proxy.VerticalAlignment,
            MinWidth = proxy.MinWidth,
            MinHeight = proxy.MinHeight,
            MaxWidth = proxy.MaxWidth,
            MaxHeight = proxy.MaxHeight,
            Width = proxy.Width,
            Height = proxy.Height,
            FontSize = Math.Max(12, proxy.FontSize),
            FontFamily = proxy.FontFamily,
            IsEnabled = proxy.IsEnabled,
            IsReadOnly = proxy.IsReadOnly,
            WordWrap = proxy.TextWrapping != TextWrapping.NoWrap,
            Background = proxy.Background ?? Brushes.Transparent,
            Foreground = proxy.Foreground,
            BorderBrush = proxy.BorderBrush,
            BorderThickness = proxy.BorderThickness,
            Padding = proxy.Padding
        };

        editor.ApplyProxyText(proxy.Text ?? string.Empty, resetDecorations: true);
        foreach (var className in proxy.Classes)
        {
            // Avalonia pseudoclasses (for example :empty, :focus, :pointerover)
            // are framework-owned state and may only be changed by the control itself.
            if (string.IsNullOrWhiteSpace(className) || className[0] == ':') continue;
            if (!editor.Classes.Contains(className)) editor.Classes.Add(className);
        }
        if (!editor.Classes.Contains("manuscript-editor")) editor.Classes.Add("manuscript-editor");

        var row = Grid.GetRow(proxy);
        var column = Grid.GetColumn(proxy);
        var rowSpan = Grid.GetRowSpan(proxy);
        var columnSpan = Grid.GetColumnSpan(proxy);
        var index = parent.Children.IndexOf(proxy);
        if (index < 0) return;

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, editor);
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, column);
        Grid.SetRowSpan(editor, rowSpan);
        Grid.SetColumnSpan(editor, columnSpan);

        _proxies.Add(proxy);
        var bridge = new EditorBridge(proxy, editor);
        _bridges.Add(bridge);

        if (_primaryEditor is null)
        {
            _primaryEditor = editor;
            UpdatePrimaryDocumentState();
        }

        ApplyRevisionLevel(editor);
    }

    private void WireRevisionPicker(IEnumerable<ComboBox> comboBoxes)
    {
        if (_revisionPicker is not null) return;
        foreach (var combo in comboBoxes)
        {
            if (!LooksLikeRevisionPicker(combo)) continue;
            _revisionPicker = combo;
            combo.SelectionChanged += (_, _) =>
            {
                foreach (var bridge in _bridges)
                    ApplyRevisionLevel(bridge.Editor);
            };
            foreach (var bridge in _bridges)
                ApplyRevisionLevel(bridge.Editor);
            return;
        }
    }

    private static bool LooksLikeRevisionPicker(ComboBox combo)
    {
        if (combo.ItemsSource is not IEnumerable items) return false;
        var labels = items.Cast<object?>().Select(static item => item?.ToString()).Where(static text => text is not null).ToArray();
        return labels.Contains("Revisions Off", StringComparer.Ordinal) && labels.Contains("Revision 1", StringComparer.Ordinal);
    }

    private void ApplyRevisionLevel(ManuscriptEditor editor)
        => editor.RevisionLevel = Math.Clamp(_revisionPicker?.SelectedIndex ?? 0, 0, 5);

    private void UpdatePrimaryDocumentState()
    {
        if (_primaryEditor is null) return;
        var row = _viewModel.SelectedRow;
        _primaryEditor.SetDocumentIdentity(row?.Node.PersistentId);
        _primaryEditor.SetLineAnnotations(row?.Node.Comments.Select(static comment =>
            new ManuscriptLineAnnotation(
                comment.Line,
                comment.Resolved ? ManuscriptAnnotationKind.Note : ManuscriptAnnotationKind.Comment,
                comment.Text)) ?? []);
    }

    private void SynchronizeBridges()
    {
        if (_disposed) return;

        var composition = _window.WindowState == WindowState.FullScreen;
        for (var index = _bridges.Count - 1; index >= 0; index--)
        {
            var bridge = _bridges[index];
            if (bridge.Editor.Parent is null)
            {
                bridge.Dispose();
                _bridges.RemoveAt(index);
                continue;
            }

            bridge.SynchronizeVisualState();
            bridge.Editor.SetCompositionMode(composition);
            ApplyRevisionLevel(bridge.Editor);
        }

        if (composition && !_wasCompositionMode)
            _primaryEditor?.Focus();
        _wasCompositionMode = composition;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _syncTimer.Stop();
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        foreach (var bridge in _bridges) bridge.Dispose();
        _bridges.Clear();
        _proxies.Clear();
    }

    private sealed class EditorBridge : IDisposable
    {
        private readonly TextBox _proxy;
        private readonly ManuscriptEditor _editor;
        private bool _synchronizing;
        private int _lastProxySelectionStart;
        private int _lastProxySelectionEnd;

        public EditorBridge(TextBox proxy, ManuscriptEditor editor)
        {
            _proxy = proxy;
            _editor = editor;
            _lastProxySelectionStart = proxy.SelectionStart;
            _lastProxySelectionEnd = proxy.SelectionEnd;
            proxy.TextChanged += ProxyTextChanged;
            editor.TextChanged += EditorTextChanged;
            editor.TextArea.Caret.PositionChanged += EditorSelectionChanged;
            editor.TextArea.SelectionChanged += EditorSelectionChanged;
            PushEditorSelectionToProxy();
        }

        public ManuscriptEditor Editor => _editor;

        private void ProxyTextChanged(object? sender, TextChangedEventArgs e)
        {
            if (_synchronizing) return;
            _synchronizing = true;
            try
            {
                _editor.ApplyProxyText(_proxy.Text ?? string.Empty);
            }
            finally
            {
                _synchronizing = false;
            }

            Dispatcher.UIThread.Post(() => PullProxySelectionToEditor(focus: true), DispatcherPriority.Background);
        }

        private void EditorTextChanged(object? sender, EventArgs e)
        {
            if (_synchronizing) return;
            _synchronizing = true;
            try
            {
                _proxy.Text = _editor.Text;
                PushEditorSelectionToProxy();
            }
            finally
            {
                _synchronizing = false;
            }
        }

        private void EditorSelectionChanged(object? sender, EventArgs e)
        {
            if (_synchronizing) return;
            PushEditorSelectionToProxy();
        }

        public void SynchronizeVisualState()
        {
            _editor.IsEnabled = _proxy.IsEnabled;
            _editor.IsReadOnly = _proxy.IsReadOnly;
            _editor.Margin = _proxy.Margin;
            _editor.MinHeight = _proxy.MinHeight;
            _editor.MaxHeight = _proxy.MaxHeight;
            _editor.Height = _proxy.Height;
            _editor.BorderBrush = _proxy.BorderBrush;
            _editor.BorderThickness = _proxy.BorderThickness;

            if (_editor.IsKeyboardFocusWithin)
            {
                PushEditorSelectionToProxy();
                return;
            }

            if (_proxy.SelectionStart != _lastProxySelectionStart || _proxy.SelectionEnd != _lastProxySelectionEnd)
                PullProxySelectionToEditor(focus: true);
        }

        private void PushEditorSelectionToProxy()
        {
            var start = Math.Clamp(_editor.SelectionStart, 0, (_proxy.Text ?? string.Empty).Length);
            var end = Math.Clamp(_editor.SelectionStart + _editor.SelectionLength, 0, (_proxy.Text ?? string.Empty).Length);
            _lastProxySelectionStart = start;
            _lastProxySelectionEnd = end;
            _proxy.SelectionStart = start;
            _proxy.SelectionEnd = end;
            _proxy.CaretIndex = Math.Clamp(_editor.CaretOffset, 0, (_proxy.Text ?? string.Empty).Length);
        }

        private void PullProxySelectionToEditor(bool focus)
        {
            if (_synchronizing) return;
            var textLength = _editor.Document.TextLength;
            var start = Math.Clamp(Math.Min(_proxy.SelectionStart, _proxy.SelectionEnd), 0, textLength);
            var end = Math.Clamp(Math.Max(_proxy.SelectionStart, _proxy.SelectionEnd), start, textLength);
            _lastProxySelectionStart = _proxy.SelectionStart;
            _lastProxySelectionEnd = _proxy.SelectionEnd;

            _synchronizing = true;
            try
            {
                _editor.Select(start, end - start);
                _editor.CaretOffset = Math.Clamp(_proxy.CaretIndex, 0, textLength);
                if (focus) _editor.Focus();
            }
            finally
            {
                _synchronizing = false;
            }
        }

        public void Dispose()
        {
            _proxy.TextChanged -= ProxyTextChanged;
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= EditorSelectionChanged;
            _editor.TextArea.SelectionChanged -= EditorSelectionChanged;
        }
    }
}
