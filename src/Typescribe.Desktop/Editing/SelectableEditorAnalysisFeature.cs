using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

/// <summary>
/// Lets authors choose which editor analysis categories are active. Spelling stays
/// delegated to <see cref="RealSpellCheckFeature"/> while grammar, style, and
/// readability checks can be enabled independently.
/// </summary>
internal sealed class SelectableEditorAnalysisFeature
{
    private static readonly (string Phrase, string Replacement, string Message)[] ConcisionRules =
    [
        ("in order to", "to", "Consider the shorter 'to'."),
        ("due to the fact that", "because", "This phrase can usually be simplified to 'because'."),
        ("at this point in time", "now", "This phrase can usually be simplified to 'now'."),
        ("for the purpose of", "to", "Consider a more direct construction."),
        ("has the ability to", "can", "Consider the more direct 'can'."),
        ("a large number of", "many", "Consider the more concise 'many'.")
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly DispatcherTimer _analysisTimer;
    private readonly List<AnalysisIssue> _issues = [];

    private ManuscriptEditor? _editor;
    private ToggleButton? _spellingToggle;
    private ToggleButton? _grammarToggle;
    private ToggleButton? _styleToggle;
    private ToggleButton? _readabilityToggle;
    private TextBlock? _status;
    private MenuItem? _spellingSourceItem;
    private MenuItem? _spellingContextItem;
    private MenuItem? _grammarContextItem;
    private MenuItem? _styleContextItem;
    private MenuItem? _readabilityContextItem;
    private string? _loadedProjectRoot;
    private long _analysisVersion;
    private int _issueIndex = -1;
    private bool _spellingEnabled = true;
    private bool _grammarEnabled;
    private bool _styleEnabled;
    private bool _readabilityEnabled;
    private bool _syncingUi;
    private bool _syncingSpellingBridge;
    private bool _installed;
    private bool _disposed;

    private SelectableEditorAnalysisFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(760) };
        _analysisTimer.Tick += async (_, _) =>
        {
            _analysisTimer.Stop();
            await RunAnalysisAsync();
        };
    }

    public static void Apply(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(repository);

        var feature = new SelectableEditorAnalysisFeature(window, viewModel, repository);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnViewModelStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        SynchronizeProject();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (!_installed) TryInstall();
        else if (_spellingSourceItem is null) TryBindSpellingToggle();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            SynchronizeProject();
            ScheduleAnalysis();
        }, DispatcherPriority.Background);
    }

    private void TryInstall()
    {
        if (_disposed || _installed) return;
        var editor = _window.GetVisualDescendants().OfType<ManuscriptEditor>()
            .FirstOrDefault(candidate => candidate.DocumentIdentity is not null)
            ?? _window.GetVisualDescendants().OfType<ManuscriptEditor>().FirstOrDefault();
        if (editor is null) return;

        var legacyAnalysis = _window.GetVisualDescendants()
            .OfType<ToggleButton>()
            .FirstOrDefault(button => string.Equals(
                button.Content?.ToString(),
                "Analysis",
                StringComparison.OrdinalIgnoreCase));
        if (legacyAnalysis?.Parent is not StackPanel panel) return;

        // ProfessionalEditorSuite owns the old all-or-nothing pass. Keep it off so
        // it cannot overwrite category-specific or dictionary spelling diagnostics.
        legacyAnalysis.IsChecked = false;
        ReplaceLegacyAnalysisControls(panel, legacyAnalysis);

        _editor = editor;
        editor.TextChanged += EditorTextChanged;
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextArea.SelectionChanged += SelectionChanged;
        ExtendContextMenu();

        _window.AddHandler(
            InputElement.KeyDownEvent,
            WindowPreviewKeyDown,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);

        _installed = true;
        SynchronizeProject();
        TryBindSpellingToggle();
        RefreshUi();
        ScheduleAnalysis();
    }

    private void ReplaceLegacyAnalysisControls(StackPanel panel, ToggleButton legacyAnalysis)
    {
        var legacyIndex = panel.Children.IndexOf(legacyAnalysis);
        if (legacyIndex < 0) return;

        // Hide the legacy Analysis toggle, issue navigation, fix action and status.
        // The next Border is the separator inserted by ProfessionalEditorSuite.
        for (var index = legacyIndex; index < panel.Children.Count; index++)
        {
            if (index > legacyIndex && panel.Children[index] is Border) break;
            panel.Children[index].IsVisible = false;
        }

        var controls = new Control[]
        {
            new TextBlock
            {
                Text = "Checks",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.72,
                Margin = new Thickness(2, 0, 3, 0)
            },
            CreateToggle("Spell", "Dictionary spell checking", OnSpellingChanged, out _spellingToggle),
            CreateToggle("Grammar", "Repeated words, spacing and punctuation", OnGrammarChanged, out _grammarToggle),
            CreateToggle("Style", "Concision and passive-construction suggestions", OnStyleChanged, out _styleToggle),
            CreateToggle("Readability", "Long sentence and dense paragraph warnings", OnReadabilityChanged, out _readabilityToggle),
            CreateButton("‹", "Previous enabled analysis issue", () => NavigateIssue(-1)),
            CreateButton("›", "Next enabled analysis issue", () => NavigateIssue(1)),
            CreateButton("Fix", "Apply the current analysis issue's suggested fix", ApplyCurrentFix),
            _status = new TextBlock
            {
                MinWidth = 92,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.72,
                Margin = new Thickness(4, 0)
            }
        };

        for (var offset = 0; offset < controls.Length; offset++)
            panel.Children.Insert(legacyIndex + offset, controls[offset]);
    }

    private static ToggleButton CreateToggle(
        string label,
        string tip,
        Action<bool> changed,
        out ToggleButton toggle)
    {
        var created = new ToggleButton
        {
            Content = label,
            Height = 27,
            MinHeight = 27,
            MinWidth = 44,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(created, tip);
        created.IsCheckedChanged += (_, _) => changed(created.IsChecked == true);
        toggle = created;
        return created;
    }

    private static Button CreateButton(string label, string tip, Action action)
    {
        var button = new Button
        {
            Content = label,
            Height = 27,
            MinHeight = 27,
            MinWidth = label.Length <= 1 ? 28 : 44,
            Padding = new Thickness(6, 1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private void OnSpellingChanged(bool enabled)
    {
        if (_syncingUi) return;
        _spellingEnabled = enabled;
        PersistSelection();
        ApplySpellingPreference();
        RefreshUi();
    }

    private void OnGrammarChanged(bool enabled)
    {
        if (_syncingUi) return;
        _grammarEnabled = enabled;
        PersistSelection();
        ScheduleAnalysis();
    }

    private void OnStyleChanged(bool enabled)
    {
        if (_syncingUi) return;
        _styleEnabled = enabled;
        PersistSelection();
        ScheduleAnalysis();
    }

    private void OnReadabilityChanged(bool enabled)
    {
        if (_syncingUi) return;
        _readabilityEnabled = enabled;
        PersistSelection();
        ScheduleAnalysis();
    }

    private void ExtendContextMenu()
    {
        if (_editor?.ContextMenu is null) return;
        var existing = _editor.ContextMenu.ItemsSource is IEnumerable enumerable
            ? enumerable.Cast<object>().ToList()
            : [];
        if (existing.OfType<MenuItem>().Any(item =>
                string.Equals(item.Header?.ToString(), "Analysis Types", StringComparison.Ordinal)))
            return;

        // These actions belong to the disabled all-or-nothing analyzer. Replace them
        // with navigation actions that operate on the enabled category set.
        foreach (var legacy in existing.OfType<MenuItem>())
        {
            if (string.Equals(legacy.Header?.ToString(), "Next Writing Issue", StringComparison.Ordinal) ||
                string.Equals(legacy.Header?.ToString(), "Apply Suggested Fix", StringComparison.Ordinal))
                legacy.IsVisible = false;
        }

        _spellingContextItem = CreateContextToggle("Spelling", OnSpellingChanged);
        _grammarContextItem = CreateContextToggle("Grammar", OnGrammarChanged);
        _styleContextItem = CreateContextToggle("Style", OnStyleChanged);
        _readabilityContextItem = CreateContextToggle("Readability", OnReadabilityChanged);

        var previous = new MenuItem { Header = "Previous Analysis Issue" };
        previous.Click += (_, _) => NavigateIssue(-1);
        var next = new MenuItem { Header = "Next Analysis Issue" };
        next.Click += (_, _) => NavigateIssue(1);
        var fix = new MenuItem { Header = "Apply Analysis Fix" };
        fix.Click += (_, _) => ApplyCurrentFix();

        var analysisMenu = new MenuItem
        {
            Header = "Analysis Types",
            ItemsSource = new object[]
            {
                _spellingContextItem,
                _grammarContextItem,
                _styleContextItem,
                _readabilityContextItem,
                new Separator(),
                previous,
                next,
                fix
            }
        };
        existing.Add(new Separator());
        existing.Add(analysisMenu);
        _editor.ContextMenu.ItemsSource = existing;
    }

    private MenuItem CreateContextToggle(string label, Action<bool> changed)
    {
        var item = new MenuItem
        {
            Header = label,
            ToggleType = MenuItemToggleType.CheckBox
        };
        item.Click += (_, _) =>
        {
            if (_syncingUi) return;
            changed(item.IsChecked == true);
        };
        return item;
    }

    private void SynchronizeProject()
    {
        var root = _repository.CurrentProject?.RootPath;
        if (string.Equals(root, _loadedProjectRoot, StringComparison.Ordinal)) return;

        _loadedProjectRoot = root;
        var selection = ProjectAnalysisSettingsStore.Load(root);
        _spellingEnabled = selection.Spelling;
        _grammarEnabled = selection.Grammar;
        _styleEnabled = selection.Style;
        _readabilityEnabled = selection.Readability;
        _issues.Clear();
        _issueIndex = -1;
        RefreshUi();
        ApplySpellingPreference();
        ScheduleAnalysis();
    }

    private void PersistSelection()
    {
        ProjectAnalysisSettingsStore.Save(
            _loadedProjectRoot,
            new AnalysisSelection(
                _spellingEnabled,
                _grammarEnabled,
                _styleEnabled,
                _readabilityEnabled));
    }

    private void TryBindSpellingToggle()
    {
        if (_editor?.ContextMenu is null || _spellingSourceItem is not null) return;
        var item = EnumerateMenuItems(_editor.ContextMenu.ItemsSource)
            .FirstOrDefault(candidate => string.Equals(
                candidate.Header?.ToString(),
                "Enable Spell Checking",
                StringComparison.Ordinal));
        if (item is null) return;

        _spellingSourceItem = item;
        item.Click += SpellingSourceItemClicked;
        ApplySpellingPreference();
    }

    private void SpellingSourceItemClicked(object? sender, RoutedEventArgs e)
    {
        if (_syncingSpellingBridge || sender is not MenuItem item) return;
        _spellingEnabled = item.IsChecked == true;
        PersistSelection();
        RefreshUi();
    }

    private void ApplySpellingPreference()
    {
        TryBindSpellingToggle();
        if (_spellingSourceItem is null || _spellingSourceItem.IsChecked == _spellingEnabled) return;

        _syncingSpellingBridge = true;
        try
        {
            _spellingSourceItem.IsChecked = _spellingEnabled;
            _spellingSourceItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally
        {
            _syncingSpellingBridge = false;
        }
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(object? source)
    {
        if (source is not IEnumerable enumerable) yield break;
        foreach (var entry in enumerable)
        {
            if (entry is not MenuItem item) continue;
            yield return item;
            foreach (var child in EnumerateMenuItems(item.ItemsSource))
                yield return child;
        }
    }

    private void EditorTextChanged(object? sender, EventArgs e) => ScheduleAnalysis();
    private void CaretPositionChanged(object? sender, EventArgs e) => RefreshUi();
    private void SelectionChanged(object? sender, EventArgs e) => RefreshUi();

    private void ScheduleAnalysis()
    {
        if (!_installed || _editor is null) return;
        Interlocked.Increment(ref _analysisVersion);
        _analysisTimer.Stop();
        if (_grammarEnabled || _styleEnabled || _readabilityEnabled)
            _analysisTimer.Start();
        else
            RemoveOwnedDiagnostics();
        RefreshUi();
    }

    private async Task RunAnalysisAsync()
    {
        if (_editor is null) return;
        var enabled = EnabledKinds();
        if (enabled == AnalysisKind.None)
        {
            RemoveOwnedDiagnostics();
            return;
        }

        var version = _analysisVersion;
        var snapshot = _editor.Text ?? string.Empty;
        var issues = await Task.Run(() => Analyze(snapshot, enabled));
        if (_disposed || version != _analysisVersion || _editor is null) return;

        _issues.Clear();
        _issues.AddRange(issues);
        if (_issueIndex >= _issues.Count) _issueIndex = _issues.Count - 1;

        var retained = _editor.Diagnostics.Where(static diagnostic => !IsOwnedDiagnostic(diagnostic));
        _editor.SpellIndicatorsEnabled = true;
        _editor.SetSpellingDiagnostics(retained.Concat(_issues.Select(issue =>
            new ManuscriptTextDiagnostic(issue.Offset, issue.Length, FormatMessage(issue)))));
        RefreshUi();
    }

    private AnalysisKind EnabledKinds()
    {
        var enabled = AnalysisKind.None;
        if (_grammarEnabled) enabled |= AnalysisKind.Grammar;
        if (_styleEnabled) enabled |= AnalysisKind.Style;
        if (_readabilityEnabled) enabled |= AnalysisKind.Readability;
        return enabled;
    }

    private static AnalysisIssue[] Analyze(string text, AnalysisKind enabled)
    {
        var issues = new List<AnalysisIssue>();
        var words = ExtractWords(text);

        if (enabled.HasFlag(AnalysisKind.Grammar)) AddGrammarIssues(text, words, issues);
        if (enabled.HasFlag(AnalysisKind.Style)) AddStyleIssues(text, words, issues);
        if (enabled.HasFlag(AnalysisKind.Readability))
        {
            AddLongSentenceIssues(text, words, issues);
            AddLongParagraphIssues(text, words, issues);
        }

        return issues
            .GroupBy(issue => (issue.Offset, issue.Length, issue.Kind, issue.Message))
            .Select(group => group.First())
            .OrderBy(issue => issue.Offset)
            .ThenBy(issue => issue.Kind)
            .ToArray();
    }

    private static void AddGrammarIssues(
        string text,
        IReadOnlyList<WordSpan> words,
        ICollection<AnalysisIssue> issues)
    {
        for (var index = 1; index < words.Count; index++)
        {
            if (!string.Equals(words[index - 1].Text, words[index].Text, StringComparison.OrdinalIgnoreCase)) continue;
            var start = words[index - 1].Start + words[index - 1].Length;
            var length = words[index].Start + words[index].Length - start;
            issues.Add(new AnalysisIssue(
                start,
                length,
                AnalysisKind.Grammar,
                $"Repeated word: {words[index].Text}",
                string.Empty));
        }

        for (var index = 0; index + 1 < text.Length; index++)
        {
            if (text[index] == ' ' && text[index + 1] == ' ')
            {
                var runEnd = index + 2;
                while (runEnd < text.Length && text[runEnd] == ' ') runEnd++;
                issues.Add(new AnalysisIssue(
                    index,
                    runEnd - index,
                    AnalysisKind.Grammar,
                    "Multiple spaces.",
                    " "));
                index = runEnd - 1;
                continue;
            }

            if (text[index] == ' ' && ",.;:!?".Contains(text[index + 1]))
            {
                issues.Add(new AnalysisIssue(
                    index,
                    1,
                    AnalysisKind.Grammar,
                    "Remove the space before punctuation.",
                    string.Empty));
            }
        }
    }

    private static void AddStyleIssues(
        string text,
        IReadOnlyList<WordSpan> words,
        ICollection<AnalysisIssue> issues)
    {
        foreach (var rule in ConcisionRules)
        {
            var search = 0;
            while (search < text.Length)
            {
                var found = text.IndexOf(rule.Phrase, search, StringComparison.OrdinalIgnoreCase);
                if (found < 0) break;
                if (IsBoundary(text, found - 1) && IsBoundary(text, found + rule.Phrase.Length))
                {
                    issues.Add(new AnalysisIssue(
                        found,
                        rule.Phrase.Length,
                        AnalysisKind.Style,
                        rule.Message,
                        rule.Replacement));
                }
                search = found + rule.Phrase.Length;
            }
        }

        for (var index = 0; index + 1 < words.Count; index++)
        {
            if (!IsBeVerb(words[index].Text) || !LooksLikeParticiple(words[index + 1].Text)) continue;
            var start = words[index].Start;
            var end = words[index + 1].Start + words[index + 1].Length;
            issues.Add(new AnalysisIssue(
                start,
                end - start,
                AnalysisKind.Style,
                "Possible passive construction. Check whether a more direct subject/verb is clearer.",
                null));
        }
    }

    private static void AddLongSentenceIssues(
        string text,
        IReadOnlyList<WordSpan> words,
        ICollection<AnalysisIssue> issues)
    {
        var sentenceStart = 0;
        for (var index = 0; index <= text.Length; index++)
        {
            var boundary = index == text.Length || text[index] is '.' or '!' or '?';
            if (!boundary) continue;
            var sentenceEnd = index == text.Length ? text.Length : index + 1;
            var count = words.Count(word => word.Start >= sentenceStart && word.Start < sentenceEnd);
            if (count > 38)
            {
                var first = words.FirstOrDefault(word => word.Start >= sentenceStart && word.Start < sentenceEnd);
                if (first is not null)
                {
                    issues.Add(new AnalysisIssue(
                        first.Start,
                        Math.Max(1, Math.Min(sentenceEnd - first.Start, 180)),
                        AnalysisKind.Readability,
                        $"Long sentence ({count} words). Consider splitting it.",
                        null));
                }
            }
            sentenceStart = sentenceEnd;
        }
    }

    private static void AddLongParagraphIssues(
        string text,
        IReadOnlyList<WordSpan> words,
        ICollection<AnalysisIssue> issues)
    {
        var start = 0;
        while (start < text.Length)
        {
            var separator = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            var end = separator < 0 ? text.Length : separator;
            var count = words.Count(word => word.Start >= start && word.Start < end);
            if (count > 180)
            {
                var first = words.FirstOrDefault(word => word.Start >= start && word.Start < end);
                if (first is not null)
                {
                    issues.Add(new AnalysisIssue(
                        first.Start,
                        Math.Min(Math.Max(1, end - first.Start), 180),
                        AnalysisKind.Readability,
                        $"Dense paragraph ({count} words). Consider a paragraph break.",
                        null));
                }
            }
            if (separator < 0) break;
            start = separator + 2;
        }
    }

    private static List<WordSpan> ExtractWords(string text)
    {
        var words = new List<WordSpan>();
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && !IsWordCharacter(text[index])) index++;
            if (index >= text.Length) break;
            var start = index;
            while (index < text.Length && IsWordCharacter(text[index])) index++;
            words.Add(new WordSpan(start, index - start, text[start..index]));
        }
        return words;
    }

    private static bool IsWordCharacter(char value)
        => char.IsLetter(value) || value is '\'' or '’' or '-';

    private static bool IsBoundary(string text, int index)
        => index < 0 || index >= text.Length || !IsWordCharacter(text[index]);

    private static bool IsBeVerb(string word)
        => word.Equals("is", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("are", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("was", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("were", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("be", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("been", StringComparison.OrdinalIgnoreCase) ||
           word.Equals("being", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeParticiple(string word)
        => word.Length > 4 &&
           (word.EndsWith("ed", StringComparison.OrdinalIgnoreCase) ||
            word.EndsWith("en", StringComparison.OrdinalIgnoreCase));

    private void RemoveOwnedDiagnostics()
    {
        if (_editor is null) return;
        var retained = _editor.Diagnostics.Where(static diagnostic => !IsOwnedDiagnostic(diagnostic)).ToArray();
        _editor.SetSpellingDiagnostics(retained);
        _issues.Clear();
        _issueIndex = -1;
        RefreshUi();
    }

    private static bool IsOwnedDiagnostic(ManuscriptTextDiagnostic diagnostic)
        => diagnostic.Message.StartsWith("Grammar: ", StringComparison.Ordinal) ||
           diagnostic.Message.StartsWith("Style: ", StringComparison.Ordinal) ||
           diagnostic.Message.StartsWith("Readability: ", StringComparison.Ordinal);

    private static string FormatMessage(AnalysisIssue issue)
        => $"{issue.Kind}: {issue.Message}";

    private void NavigateIssue(int direction)
    {
        if (_editor is null || _issues.Count == 0) return;
        var caret = _editor.CaretOffset;
        if (_issueIndex < 0 || _issueIndex >= _issues.Count)
        {
            _issueIndex = _issues.FindIndex(issue => issue.Offset >= caret);
            if (_issueIndex < 0) _issueIndex = 0;
            if (direction < 0)
                _issueIndex = (_issueIndex - 1 + _issues.Count) % _issues.Count;
        }
        else
        {
            _issueIndex = (_issueIndex + direction + _issues.Count) % _issues.Count;
        }

        SelectIssue(_issues[_issueIndex]);
        RefreshUi();
    }

    private AnalysisIssue? ResolveCurrentIssue()
    {
        if (_editor is null || _issues.Count == 0) return null;
        var caret = _editor.CaretOffset;
        for (var index = 0; index < _issues.Count; index++)
        {
            var issue = _issues[index];
            if (caret < issue.Offset || caret > issue.Offset + Math.Max(1, issue.Length)) continue;
            _issueIndex = index;
            return issue;
        }
        return _issueIndex >= 0 && _issueIndex < _issues.Count ? _issues[_issueIndex] : null;
    }

    private void SelectIssue(AnalysisIssue issue)
    {
        if (_editor is null) return;
        var offset = Math.Clamp(issue.Offset, 0, _editor.Document.TextLength);
        var length = Math.Clamp(issue.Length, 0, _editor.Document.TextLength - offset);
        _editor.Select(offset, length);
        _editor.CaretOffset = offset + length;
        var location = _editor.Document.GetLocation(offset);
        _editor.ScrollTo(location.Line, location.Column);
        _editor.Focus();
        if (_status is not null) ToolTip.SetTip(_status, FormatMessage(issue));
    }

    private void ApplyCurrentFix()
    {
        if (_editor is null) return;
        var issue = ResolveCurrentIssue();
        if (issue?.Replacement is null) return;
        var offset = Math.Clamp(issue.Offset, 0, _editor.Document.TextLength);
        var length = Math.Clamp(issue.Length, 0, _editor.Document.TextLength - offset);
        _editor.Document.Replace(offset, length, issue.Replacement);
        _editor.CaretOffset = offset + issue.Replacement.Length;
        _editor.Focus();
    }

    private void WindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editor is null || !_viewModel.HasDocument || e.Key != Key.F7) return;
        if (!_grammarEnabled && !_styleEnabled && !_readabilityEnabled) return;
        e.Handled = true;
        NavigateIssue(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
    }

    private void RefreshUi()
    {
        _syncingUi = true;
        try
        {
            SetChecked(_spellingToggle, _spellingEnabled);
            SetChecked(_grammarToggle, _grammarEnabled);
            SetChecked(_styleToggle, _styleEnabled);
            SetChecked(_readabilityToggle, _readabilityEnabled);
            SetChecked(_spellingContextItem, _spellingEnabled);
            SetChecked(_grammarContextItem, _grammarEnabled);
            SetChecked(_styleContextItem, _styleEnabled);
            SetChecked(_readabilityContextItem, _readabilityEnabled);
        }
        finally
        {
            _syncingUi = false;
        }

        if (_status is null) return;
        var enabledCount = (_grammarEnabled ? 1 : 0) + (_styleEnabled ? 1 : 0) + (_readabilityEnabled ? 1 : 0);
        _status.Text = enabledCount == 0
            ? "writing off"
            : _issues.Count == 1 ? "1 issue" : $"{_issues.Count} issues";
        var current = ResolveCurrentIssue();
        if (current is not null) ToolTip.SetTip(_status, FormatMessage(current));
    }

    private static void SetChecked(ToggleButton? item, bool value)
    {
        if (item is not null && item.IsChecked != value) item.IsChecked = value;
    }

    private static void SetChecked(MenuItem? item, bool value)
    {
        if (item is not null && item.IsChecked != value) item.IsChecked = value;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _analysisTimer.Stop();
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;
        _window.RemoveHandler(InputElement.KeyDownEvent, WindowPreviewKeyDown);

        if (_spellingSourceItem is not null)
            _spellingSourceItem.Click -= SpellingSourceItemClicked;

        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextArea.SelectionChanged -= SelectionChanged;
        }
    }

    [Flags]
    private enum AnalysisKind
    {
        None = 0,
        Grammar = 1,
        Style = 2,
        Readability = 4
    }

    private sealed record WordSpan(int Start, int Length, string Text);

    private sealed record AnalysisIssue(
        int Offset,
        int Length,
        AnalysisKind Kind,
        string Message,
        string? Replacement);
}

internal static class ProjectAnalysisSettingsStore
{
    public static AnalysisSelection Load(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return AnalysisSelection.Default;
        try
        {
            var path = SettingsPath(projectRoot);
            if (!File.Exists(path)) return AnalysisSelection.Default;
            var values = File.ReadAllLines(path)
                .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

            return new AnalysisSelection(
                ReadBool(values, "spelling", defaultValue: true),
                ReadBool(values, "grammar", defaultValue: false),
                ReadBool(values, "style", defaultValue: false),
                ReadBool(values, "readability", defaultValue: false));
        }
        catch
        {
            return AnalysisSelection.Default;
        }
    }

    public static void Save(string? projectRoot, AnalysisSelection selection)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return;
        try
        {
            var path = SettingsPath(projectRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllLines(temporary,
            [
                "version=1",
                $"spelling={selection.Spelling.ToString().ToLowerInvariant()}",
                $"grammar={selection.Grammar.ToString().ToLowerInvariant()}",
                $"style={selection.Style.ToString().ToLowerInvariant()}",
                $"readability={selection.Readability.ToString().ToLowerInvariant()}"
            ]);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Analysis preferences should never interrupt authoring.
        }
    }

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values,
        string key,
        bool defaultValue)
        => values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : defaultValue;

    private static string SettingsPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "analysis.conf");
}

internal sealed record AnalysisSelection(
    bool Spelling,
    bool Grammar,
    bool Style,
    bool Readability)
{
    public static AnalysisSelection Default { get; } = new(
        Spelling: true,
        Grammar: false,
        Style: false,
        Readability: false);
}
