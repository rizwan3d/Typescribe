using System.Collections;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Typescribe.Desktop.ViewModels;

namespace Typescribe.Desktop.Editing;

internal interface ISpellingProvider
{
    bool IsCorrect(string word, string language);
    IReadOnlyList<string> Suggest(string word, string language, int maximumSuggestions = 5);
}

internal interface IUserDictionary
{
    bool Contains(string word, string language);
    IReadOnlyCollection<string> GetWords(string language);
    void Add(string word, string language);
}

internal sealed class LocalUserDictionary : IUserDictionary
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, HashSet<string>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalUserDictionary(string? path = null)
    {
        _path = path ?? DefaultPath();
        Load();
    }

    public bool Contains(string word, string language)
    {
        var normalized = NormalizeWord(word);
        if (normalized.Length == 0) return false;
        lock (_gate)
            return _entries.TryGetValue(NormalizeLanguage(language), out var words) && words.Contains(normalized);
    }

    public IReadOnlyCollection<string> GetWords(string language)
    {
        lock (_gate)
            return _entries.TryGetValue(NormalizeLanguage(language), out var words)
                ? words.ToArray()
                : [];
    }

    public void Add(string word, string language)
    {
        var normalized = NormalizeWord(word);
        if (normalized.Length == 0) return;
        var normalizedLanguage = NormalizeLanguage(language);

        lock (_gate)
        {
            if (!_entries.TryGetValue(normalizedLanguage, out var words))
            {
                words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _entries[normalizedLanguage] = words;
            }

            if (!words.Add(normalized)) return;
            Persist();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            foreach (var line in File.ReadLines(_path))
            {
                var separator = line.IndexOf('\t');
                if (separator <= 0 || separator >= line.Length - 1) continue;
                var language = NormalizeLanguage(line[..separator]);
                var word = NormalizeWord(line[(separator + 1)..]);
                if (word.Length == 0) continue;
                if (!_entries.TryGetValue(language, out var words))
                {
                    words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    _entries[language] = words;
                }
                words.Add(word);
            }
        }
        catch
        {
            // A damaged user dictionary must not prevent the editor from opening.
        }
    }

    private void Persist()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var lines = _entries
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SelectMany(pair => pair.Value
                    .OrderBy(static word => word, StringComparer.OrdinalIgnoreCase)
                    .Select(word => $"{pair.Key}\t{word}"));
            var temporary = _path + ".tmp";
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            // Spelling remains usable in-memory if persistence is unavailable.
        }
    }

    private static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root)) root = Path.GetTempPath();
        return Path.Combine(root, "Typescribe", "spelling", "user-dictionary.tsv");
    }

    internal static string NormalizeLanguage(string language)
        => string.IsNullOrWhiteSpace(language) ? "en-US" : language.Trim().Replace('_', '-');

    internal static string NormalizeWord(string word)
        => word.Trim().Replace('’', '\'').Trim('\'').ToLowerInvariant();
}

internal sealed class LocalDictionarySpellingProvider : ISpellingProvider
{
    private static readonly IReadOnlyDictionary<string, string> PreferredCorrections =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["teh"] = "the",
            ["adn"] = "and",
            ["recieve"] = "receive",
            ["recieved"] = "received",
            ["definately"] = "definitely",
            ["seperate"] = "separate",
            ["seperately"] = "separately",
            ["occured"] = "occurred",
            ["untill"] = "until",
            ["alot"] = "a lot",
            ["wierd"] = "weird",
            ["becuase"] = "because",
            ["thier"] = "their",
            ["freind"] = "friend",
            ["wich"] = "which",
            ["adress"] = "address",
            ["accomodate"] = "accommodate",
            ["begining"] = "beginning",
            ["goverment"] = "government",
            ["enviroment"] = "environment",
            ["existance"] = "existence"
        };

    private const string FallbackEnglishWords = """
a able about above accept according account across act action actually add after again against age ago agree air all allow almost along already also although always am among an and another answer any anyone anything appear are area around as ask at away
back bad base be because become been before begin being believe best better between big bit book both bring build business but by
call came can cannot case cause change chapter check child choose city clear close come common company consider continue could country course create current
day did different do document does done down during
each early edit editor either else end enough even ever every example experience explain
fact far feel few field find first follow for form found four from full further
general get give go good got great group grow
had hand happen has have he head help her here high him his home how however
i idea if important in include information into is issue it its
just
keep kind know known
language large last later learn least left less let life like likely line list little local long look
made make many may me mean menu method might misspelling more most move much must my
name need never new next no not note now number
of off often old on once one only open or order other our out over own
page part people perhaps place point possible present problem project provide public put
question
read real really reason right rule run
same say see seem set several she should show simple since small so some something spell spelling start state still style such suggestion support system
take text than that the their them then there these they thing think this those through time to too try two type
under until up use user
very
want was way we well were what when where which while who why will with word work world would write writing
yes yet you your
author authors authoring manuscript manuscripts sentence sentences paragraph paragraphs grammar readability correction corrections dictionary dictionaries ignore ignored ignoring language languages provider providers local project projects document documents suggestion suggestions misspell misspelled misspelling misspellings arbitrary analysis engine engines
add added adding remove removed replace replaced replacement current selected selection caret context menu file files path root save saved load loaded setting settings
english american british color colour center centre favorite favourite behavior behaviour organize organise recognize recognise
don't doesn't didn't can't won't isn't aren't wasn't weren't shouldn't couldn't wouldn't i'm you're we're they're i've we've they've i'll you'll we'll they'll
""";

    private readonly IUserDictionary _userDictionary;
    private readonly string? _projectRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, LanguageDictionary> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public LocalDictionarySpellingProvider(IUserDictionary userDictionary, string? projectRoot = null)
    {
        _userDictionary = userDictionary;
        _projectRoot = projectRoot;
    }

    public bool IsCorrect(string word, string language)
    {
        if (string.IsNullOrWhiteSpace(word)) return true;
        if (word.Contains('-'))
            return word.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .All(part => IsCorrect(part, language));

        var normalized = LocalUserDictionary.NormalizeWord(word);
        if (normalized.Length <= 1) return true;
        if (_userDictionary.Contains(normalized, language)) return true;
        if (LooksLikeIdentifier(word)) return true;

        var dictionary = GetDictionary(language);
        if (!dictionary.IsUsable) return true;
        if (dictionary.Words.Contains(normalized)) return true;
        if (AcceptContraction(normalized, dictionary.Words)) return true;
        return AcceptInflection(normalized, dictionary.Words);
    }

    public IReadOnlyList<string> Suggest(string word, string language, int maximumSuggestions = 5)
    {
        maximumSuggestions = Math.Clamp(maximumSuggestions, 1, 10);
        var normalized = LocalUserDictionary.NormalizeWord(word);
        if (normalized.Length < 2) return [];

        var suggestions = new List<string>(maximumSuggestions);
        if (PreferredCorrections.TryGetValue(normalized, out var preferred))
            suggestions.Add(preferred);

        var dictionary = GetDictionary(language);
        if (!dictionary.IsUsable) return suggestions;

        var first = normalized[0];
        if (!dictionary.Buckets.TryGetValue(first, out var candidates))
            candidates = dictionary.Words.Take(4000).ToArray();

        var maximumDistance = normalized.Length <= 4 ? 1 : normalized.Length <= 8 ? 2 : 3;
        var ranked = candidates
            .Where(candidate => Math.Abs(candidate.Length - normalized.Length) <= maximumDistance)
            .Select(candidate => (Word: candidate, Distance: EditDistance(normalized, candidate)))
            .Where(item => item.Distance <= maximumDistance)
            .OrderBy(item => item.Distance)
            .ThenBy(item => Math.Abs(item.Word.Length - normalized.Length))
            .ThenBy(item => item.Word, StringComparer.OrdinalIgnoreCase);

        foreach (var item in ranked)
        {
            if (suggestions.Contains(item.Word, StringComparer.OrdinalIgnoreCase)) continue;
            suggestions.Add(item.Word);
            if (suggestions.Count >= maximumSuggestions) break;
        }

        return suggestions;
    }

    private LanguageDictionary GetDictionary(string language)
    {
        var normalizedLanguage = LocalUserDictionary.NormalizeLanguage(language);
        lock (_gate)
        {
            if (_cache.TryGetValue(normalizedLanguage, out var cached)) return cached;
            var loaded = LoadDictionary(normalizedLanguage);
            _cache[normalizedLanguage] = loaded;
            return loaded;
        }
    }

    private LanguageDictionary LoadDictionary(string language)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var externalLoaded = false;

        foreach (var path in DictionaryCandidates(language))
        {
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var line in File.ReadLines(path))
                {
                    var candidate = line.Trim();
                    if (candidate.Length == 0 || int.TryParse(candidate, out _)) continue;
                    var slash = candidate.IndexOf('/');
                    if (slash >= 0) candidate = candidate[..slash];
                    var whitespace = candidate.IndexOfAny([' ', '\t']);
                    if (whitespace >= 0) candidate = candidate[..whitespace];
                    var normalized = LocalUserDictionary.NormalizeWord(candidate);
                    if (normalized.Length > 0 && normalized.All(IsDictionaryCharacter))
                        words.Add(normalized);
                }
                if (words.Count > 0) externalLoaded = true;
            }
            catch
            {
                // Try the next local dictionary source.
            }
        }

        var isEnglish = language.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (isEnglish)
        {
            foreach (var word in FallbackEnglishWords.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
                words.Add(LocalUserDictionary.NormalizeWord(word));
            if (language.Equals("en-GB", StringComparison.OrdinalIgnoreCase) ||
                language.Equals("en-AU", StringComparison.OrdinalIgnoreCase))
            {
                words.UnionWith(["colour", "favour", "favourite", "centre", "organise", "recognise", "behaviour"]);
            }
        }

        foreach (var userWord in _userDictionary.GetWords(language))
            words.Add(LocalUserDictionary.NormalizeWord(userWord));

        var buckets = words
            .Where(static word => word.Length > 0)
            .GroupBy(static word => word[0])
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(static word => word, StringComparer.OrdinalIgnoreCase).ToArray());

        return new LanguageDictionary(words, buckets, externalLoaded || isEnglish);
    }

    private IEnumerable<string> DictionaryCandidates(string language)
    {
        var underscore = language.Replace('-', '_');
        var shortLanguage = language.Split('-', 2)[0];
        var names = new[] { language, underscore, shortLanguage }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static name => name + ".dic")
            .ToArray();

        var configured = Environment.GetEnvironmentVariable("TYPESCRIBE_DICTIONARY");
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;

        if (!string.IsNullOrWhiteSpace(_projectRoot))
        {
            foreach (var name in names)
                yield return Path.Combine(_projectRoot, ".typescribe", "dictionaries", name);
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(local))
        {
            foreach (var name in names)
                yield return Path.Combine(local, "Typescribe", "dictionaries", name);
        }

        foreach (var directory in new[]
                 {
                     "/usr/share/hunspell",
                     "/usr/share/myspell",
                     "/usr/share/myspell/dicts",
                     "/usr/local/share/hunspell",
                     "/opt/homebrew/share/hunspell",
                     "/Library/Spelling"
                 })
        {
            foreach (var name in names)
                yield return Path.Combine(directory, name);
        }

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 }.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            foreach (var name in names)
            {
                yield return Path.Combine(programFiles, "LibreOffice", "share", "extensions", "dict-en", name);
                yield return Path.Combine(programFiles, "Mozilla Firefox", "dictionaries", name);
            }
        }
    }

    private static bool AcceptContraction(string word, IReadOnlySet<string> words)
    {
        if (!word.Contains('\'')) return false;
        if (words.Contains(word)) return true;

        foreach (var suffix in new[] { "'s", "'re", "'ve", "'ll", "'d", "'m" })
        {
            if (word.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                words.Contains(word[..^suffix.Length]))
                return true;
        }

        return false;
    }

    private static bool AcceptInflection(string word, IReadOnlySet<string> words)
    {
        if (word.Length > 3 && word.EndsWith('s') && words.Contains(word[..^1])) return true;
        if (word.Length > 4 && word.EndsWith("es", StringComparison.Ordinal) && words.Contains(word[..^2])) return true;
        if (word.Length > 4 && word.EndsWith("ies", StringComparison.Ordinal) && words.Contains(word[..^3] + "y")) return true;
        if (word.Length > 4 && word.EndsWith("ed", StringComparison.Ordinal))
        {
            var stem = word[..^2];
            if (words.Contains(stem) || words.Contains(stem + "e") || IsDoubledStem(stem, words)) return true;
        }
        if (word.Length > 5 && word.EndsWith("ing", StringComparison.Ordinal))
        {
            var stem = word[..^3];
            if (words.Contains(stem) || words.Contains(stem + "e") || IsDoubledStem(stem, words)) return true;
        }
        if (word.Length > 4 && word.EndsWith("ly", StringComparison.Ordinal) && words.Contains(word[..^2])) return true;
        if (word.Length > 4 && word.EndsWith("er", StringComparison.Ordinal) && words.Contains(word[..^2])) return true;
        if (word.Length > 5 && word.EndsWith("est", StringComparison.Ordinal) && words.Contains(word[..^3])) return true;
        return false;
    }

    private static bool IsDoubledStem(string stem, IReadOnlySet<string> words)
        => stem.Length > 2 && stem[^1] == stem[^2] && words.Contains(stem[..^1]);

    private static bool LooksLikeIdentifier(string word)
    {
        if (word.Length <= 1) return true;
        var letters = word.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return true;
        if (letters.Length <= 5 && letters.All(char.IsUpper)) return true;
        return letters.Skip(1).Any(char.IsUpper);
    }

    private static bool IsDictionaryCharacter(char value)
        => char.IsLetter(value) || value is '\'' or '’' or '-';

    private static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++) previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(previous[column] + 1, current[column - 1] + 1),
                    previous[column - 1] + cost);

                if (row > 1 && column > 1 &&
                    left[row - 1] == right[column - 2] &&
                    left[row - 2] == right[column - 1])
                {
                    current[column] = Math.Min(current[column], previous[column - 2] + 1);
                }
            }
            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private sealed record LanguageDictionary(
        HashSet<string> Words,
        Dictionary<char, string[]> Buckets,
        bool IsUsable);
}

internal sealed class RealSpellCheckFeature
{
    private static readonly (string Tag, string Label)[] SupportedLanguages =
    [
        ("en-US", "English (United States)"),
        ("en-GB", "English (United Kingdom)"),
        ("en-CA", "English (Canada)"),
        ("en-AU", "English (Australia)")
    ];

    private readonly StudioWorkspaceWindow _window;
    private readonly WorkspaceViewModel _viewModel;
    private readonly TrackingProjectRepository _repository;
    private readonly DispatcherTimer _analysisTimer;
    private readonly IUserDictionary _userDictionary;
    private readonly ProjectSpellingSettingsStore _settings = new();
    private readonly HashSet<IgnoredOccurrence> _ignoredOnce = [];
    private readonly HashSet<string> _ignoredAll = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SpellingIssue> _issues = [];
    private readonly List<MenuItem> _suggestionItems = [];

    private ManuscriptEditor? _editor;
    private ISpellingProvider? _provider;
    private MenuItem? _spellingMenu;
    private MenuItem? _addToDictionary;
    private MenuItem? _ignoreOnce;
    private MenuItem? _ignoreAll;
    private MenuItem? _enableItem;
    private string? _loadedIdentity;
    private string? _loadedProjectRoot;
    private long _analysisVersion;
    private bool _enabled = true;
    private bool _installed;
    private bool _disposed;

    private RealSpellCheckFeature(
        StudioWorkspaceWindow window,
        WorkspaceViewModel viewModel,
        TrackingProjectRepository repository)
    {
        _window = window;
        _viewModel = viewModel;
        _repository = repository;
        _userDictionary = new LocalUserDictionary();
        _analysisTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(880) };
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

        var feature = new RealSpellCheckFeature(window, viewModel, repository);
        window.Opened += feature.OnOpened;
        window.LayoutUpdated += feature.OnLayoutUpdated;
        window.Closed += feature.OnClosed;
        viewModel.StateChanged += feature.OnViewModelStateChanged;
        feature.TryInstall();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        TryInstall();
        SynchronizeDocument();
    }

    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        if (!_installed) TryInstall();
    }

    private void OnViewModelStateChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            SynchronizeDocument();
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

        _editor = editor;
        editor.TextChanged += EditorTextChanged;
        editor.TextArea.Caret.PositionChanged += CaretPositionChanged;
        editor.TextArea.SelectionChanged += SelectionChanged;
        ExtendContextMenu();

        _installed = true;
        SynchronizeDocument();
        ScheduleAnalysis();
        RefreshSpellingMenu();
    }

    private void EditorTextChanged(object? sender, EventArgs e)
    {
        ScheduleAnalysis();
        RefreshSpellingMenu();
    }

    private void CaretPositionChanged(object? sender, EventArgs e) => RefreshSpellingMenu();
    private void SelectionChanged(object? sender, EventArgs e) => RefreshSpellingMenu();

    private void SynchronizeDocument()
    {
        if (_editor is null) return;
        var identity = _editor.DocumentIdentity;
        var root = _repository.CurrentProject?.RootPath;

        if (!string.Equals(root, _loadedProjectRoot, StringComparison.Ordinal))
        {
            _loadedProjectRoot = root;
            _settings.SetProjectRoot(root);
            _provider = new LocalDictionarySpellingProvider(_userDictionary, root);
            _ignoredOnce.Clear();
            _ignoredAll.Clear();
        }

        if (string.Equals(identity, _loadedIdentity, StringComparison.Ordinal)) return;
        _loadedIdentity = identity;
        _ignoredOnce.Clear();
        _ignoredAll.Clear();
        RefreshSpellingMenu();
    }

    private void ScheduleAnalysis()
    {
        if (!_installed || _editor is null) return;
        Interlocked.Increment(ref _analysisVersion);
        _analysisTimer.Stop();
        if (_enabled) _analysisTimer.Start();
        else RemoveSpellDiagnostics();
    }

    private async Task RunAnalysisAsync()
    {
        if (!_enabled || _editor is null || _provider is null) return;
        var version = _analysisVersion;
        var snapshot = _editor.Text ?? string.Empty;
        var identity = _editor.DocumentIdentity ?? string.Empty;
        var language = CurrentLanguage();
        var ignoredOnce = _ignoredOnce.ToHashSet();
        var ignoredAll = _ignoredAll.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var provider = _provider;

        var issues = await Task.Run(() =>
            Analyze(snapshot, identity, language, provider, ignoredOnce, ignoredAll));

        if (_disposed || version != _analysisVersion || _editor is null) return;
        _issues.Clear();
        _issues.AddRange(issues);

        var retained = _editor.Diagnostics.Where(static diagnostic => !IsLegacyOrProviderSpellingDiagnostic(diagnostic));
        _editor.SpellIndicatorsEnabled = true;
        _editor.SetSpellingDiagnostics(retained.Concat(_issues.Select(issue =>
            new ManuscriptTextDiagnostic(issue.Offset, issue.Length, issue.Message))));
        RefreshSpellingMenu();
    }

    private static IReadOnlyList<SpellingIssue> Analyze(
        string text,
        string identity,
        string language,
        ISpellingProvider provider,
        IReadOnlySet<IgnoredOccurrence> ignoredOnce,
        IReadOnlySet<string> ignoredAll)
    {
        var issues = new List<SpellingIssue>();
        foreach (var word in ExtractWords(text))
        {
            if (ignoredAll.Contains(word.Text)) continue;
            if (ignoredOnce.Contains(new IgnoredOccurrence(identity, word.Start, word.Text))) continue;
            if (provider.IsCorrect(word.Text, language)) continue;

            var suggestions = provider.Suggest(word.Text, language, 5);
            var message = suggestions.Count == 0
                ? $"Misspelled word: {word.Text}"
                : $"Misspelled word: {word.Text}. Suggestions: {string.Join(", ", suggestions.Take(3))}";
            issues.Add(new SpellingIssue(word.Start, word.Length, word.Text, suggestions, message));
        }
        return issues;
    }

    private static IEnumerable<WordSpan> ExtractWords(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            while (index < text.Length && !IsWordCharacter(text[index])) index++;
            if (index >= text.Length) yield break;

            var start = index;
            while (index < text.Length && IsWordCharacter(text[index])) index++;
            var value = text[start..index].Trim('\'', '’', '-');
            if (value.Length <= 1) continue;

            var leadingTrim = 0;
            while (start + leadingTrim < index && !char.IsLetter(text[start + leadingTrim])) leadingTrim++;
            if (leadingTrim >= index - start) continue;
            var actualStart = start + leadingTrim;
            yield return new WordSpan(actualStart, value.Length, value);
        }
    }

    private static bool IsWordCharacter(char value)
        => char.IsLetter(value) || value is '\'' or '’' or '-';

    private void ExtendContextMenu()
    {
        if (_editor?.ContextMenu is null) return;
        var existing = _editor.ContextMenu.ItemsSource is IEnumerable enumerable
            ? enumerable.Cast<object>().ToList()
            : [];
        if (existing.OfType<MenuItem>().Any(item =>
                string.Equals(item.Header?.ToString(), "Spelling", StringComparison.Ordinal)))
            return;

        _enableItem = new MenuItem
        {
            Header = "Enable Spell Checking",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = true
        };
        _enableItem.Click += (_, _) =>
        {
            _enabled = _enableItem.IsChecked == true;
            ScheduleAnalysis();
            RefreshSpellingMenu();
        };

        for (var index = 0; index < 5; index++)
        {
            var item = new MenuItem { Header = "No suggestion", IsEnabled = false };
            item.Click += (_, _) =>
            {
                if (item.Header is string suggestion && item.IsEnabled)
                    ApplySuggestion(suggestion);
            };
            _suggestionItems.Add(item);
        }

        _addToDictionary = new MenuItem { Header = "Add to Dictionary" };
        _addToDictionary.Click += (_, _) => AddCurrentWordToDictionary();

        _ignoreOnce = new MenuItem { Header = "Ignore Once" };
        _ignoreOnce.Click += (_, _) => IgnoreCurrentWordOnce();

        _ignoreAll = new MenuItem { Header = "Ignore All" };
        _ignoreAll.Click += (_, _) => IgnoreCurrentWordAll();

        var documentLanguage = new MenuItem { Header = "Document Language" };
        var documentLanguageItems = new List<object>();
        var inherit = new MenuItem { Header = "Use Project Language" };
        inherit.Click += (_, _) => SetDocumentLanguage(null);
        documentLanguageItems.Add(inherit);
        foreach (var language in SupportedLanguages)
        {
            var item = new MenuItem { Header = language.Label };
            item.Click += (_, _) => SetDocumentLanguage(language.Tag);
            documentLanguageItems.Add(item);
        }
        documentLanguage.ItemsSource = documentLanguageItems;

        var projectLanguage = new MenuItem { Header = "Project Language" };
        var projectLanguageItems = new List<object>();
        foreach (var language in SupportedLanguages)
        {
            var item = new MenuItem { Header = language.Label };
            item.Click += (_, _) => SetProjectLanguage(language.Tag);
            projectLanguageItems.Add(item);
        }
        projectLanguage.ItemsSource = projectLanguageItems;

        var spellingItems = new List<object> { _enableItem };
        spellingItems.Add(new Separator());
        spellingItems.AddRange(_suggestionItems);
        spellingItems.Add(new Separator());
        spellingItems.Add(_addToDictionary);
        spellingItems.Add(_ignoreOnce);
        spellingItems.Add(_ignoreAll);
        spellingItems.Add(new Separator());
        spellingItems.Add(documentLanguage);
        spellingItems.Add(projectLanguage);

        _spellingMenu = new MenuItem
        {
            Header = "Spelling",
            ItemsSource = spellingItems
        };
        existing.Add(new Separator());
        existing.Add(_spellingMenu);
        _editor.ContextMenu.ItemsSource = existing;
    }

    private void RefreshSpellingMenu()
    {
        if (_editor is null || _spellingMenu is null) return;
        var current = ResolveIssueAtCaret();
        var word = ResolveWordAtCaret();

        if (_enableItem is not null && _enableItem.IsChecked != _enabled)
            _enableItem.IsChecked = _enabled;

        for (var index = 0; index < _suggestionItems.Count; index++)
        {
            var item = _suggestionItems[index];
            if (current is not null && index < current.Suggestions.Count)
            {
                item.Header = current.Suggestions[index];
                item.IsEnabled = true;
                item.IsVisible = true;
            }
            else
            {
                item.Header = "No suggestion";
                item.IsEnabled = false;
                item.IsVisible = index == 0;
            }
        }

        var canAct = _enabled && word is not null && _provider is not null &&
                     !_provider.IsCorrect(word.Text, CurrentLanguage());
        if (_addToDictionary is not null) _addToDictionary.IsEnabled = canAct;
        if (_ignoreOnce is not null) _ignoreOnce.IsEnabled = canAct;
        if (_ignoreAll is not null) _ignoreAll.IsEnabled = canAct;

        _spellingMenu.Header = $"Spelling ({CurrentLanguage()})";
    }

    private SpellingIssue? ResolveIssueAtCaret()
    {
        if (_editor is null) return null;
        var caret = _editor.CaretOffset;
        return _issues.FirstOrDefault(issue =>
            caret >= issue.Offset && caret <= issue.Offset + Math.Max(1, issue.Length));
    }

    private WordSpan? ResolveWordAtCaret()
    {
        if (_editor is null) return null;
        var text = _editor.Text ?? string.Empty;
        if (text.Length == 0) return null;

        var caret = Math.Clamp(_editor.CaretOffset, 0, text.Length);
        if (caret == text.Length || !IsWordCharacter(text[caret]))
        {
            if (caret == 0 || !IsWordCharacter(text[caret - 1])) return null;
            caret--;
        }

        var start = caret;
        var end = caret + 1;
        while (start > 0 && IsWordCharacter(text[start - 1])) start--;
        while (end < text.Length && IsWordCharacter(text[end])) end++;
        var value = text[start..end].Trim('\'', '’', '-');
        while (start < end && !char.IsLetter(text[start])) start++;
        if (value.Length <= 1 || start >= end) return null;
        return new WordSpan(start, value.Length, value);
    }

    private void ApplySuggestion(string suggestion)
    {
        if (_editor is null) return;
        var word = ResolveWordAtCaret();
        if (word is null || string.IsNullOrWhiteSpace(suggestion)) return;

        var replacement = MatchCase(word.Text, suggestion);
        _editor.Document.Replace(word.Start, word.Length, replacement);
        _editor.CaretOffset = word.Start + replacement.Length;
        _editor.Focus();
        ScheduleAnalysis();
    }

    private void AddCurrentWordToDictionary()
    {
        var word = ResolveWordAtCaret();
        if (word is null) return;
        _userDictionary.Add(word.Text, CurrentLanguage());
        ScheduleAnalysis();
    }

    private void IgnoreCurrentWordOnce()
    {
        if (_editor is null) return;
        var word = ResolveWordAtCaret();
        if (word is null) return;
        _ignoredOnce.Add(new IgnoredOccurrence(_editor.DocumentIdentity ?? string.Empty, word.Start, word.Text));
        ScheduleAnalysis();
    }

    private void IgnoreCurrentWordAll()
    {
        var word = ResolveWordAtCaret();
        if (word is null) return;
        _ignoredAll.Add(word.Text);
        ScheduleAnalysis();
    }

    private void SetDocumentLanguage(string? language)
    {
        if (_editor?.DocumentIdentity is not { Length: > 0 } identity) return;
        _settings.SetDocumentLanguage(identity, language);
        _ignoredOnce.Clear();
        _ignoredAll.Clear();
        ScheduleAnalysis();
        RefreshSpellingMenu();
    }

    private void SetProjectLanguage(string language)
    {
        _settings.SetProjectLanguage(language);
        _ignoredOnce.Clear();
        _ignoredAll.Clear();
        ScheduleAnalysis();
        RefreshSpellingMenu();
    }

    private string CurrentLanguage()
        => _settings.ResolveLanguage(_editor?.DocumentIdentity);

    private void RemoveSpellDiagnostics()
    {
        if (_editor is null) return;
        var retained = _editor.Diagnostics.Where(static diagnostic => !IsLegacyOrProviderSpellingDiagnostic(diagnostic)).ToArray();
        _editor.SetSpellingDiagnostics(retained);
        _issues.Clear();
    }

    private static bool IsLegacyOrProviderSpellingDiagnostic(ManuscriptTextDiagnostic diagnostic)
        => diagnostic.Message.StartsWith("Possible spelling issue:", StringComparison.OrdinalIgnoreCase) ||
           diagnostic.Message.StartsWith("Misspelled word:", StringComparison.OrdinalIgnoreCase);

    private static string MatchCase(string source, string replacement)
    {
        if (source.All(character => !char.IsLetter(character) || char.IsUpper(character)))
            return replacement.ToUpperInvariant();
        if (source.Length > 0 && char.IsUpper(source[0]) && replacement.Length > 0)
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        return replacement;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _disposed = true;
        _analysisTimer.Stop();
        _viewModel.StateChanged -= OnViewModelStateChanged;
        _window.Opened -= OnOpened;
        _window.LayoutUpdated -= OnLayoutUpdated;
        _window.Closed -= OnClosed;

        if (_editor is not null)
        {
            _editor.TextChanged -= EditorTextChanged;
            _editor.TextArea.Caret.PositionChanged -= CaretPositionChanged;
            _editor.TextArea.SelectionChanged -= SelectionChanged;
        }
    }

    private sealed record WordSpan(int Start, int Length, string Text);
    private sealed record IgnoredOccurrence(string Identity, int Offset, string Word);
    private sealed record SpellingIssue(
        int Offset,
        int Length,
        string Word,
        IReadOnlyList<string> Suggestions,
        string Message);
}

internal sealed class ProjectSpellingSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private string? _projectRoot;
    private SpellingSettingsState _state = SpellingSettingsState.Default;

    public void SetProjectRoot(string? projectRoot)
    {
        if (string.Equals(_projectRoot, projectRoot, StringComparison.Ordinal)) return;
        _projectRoot = projectRoot;
        _state = Load(projectRoot);
    }

    public string ResolveLanguage(string? documentIdentity)
    {
        if (!string.IsNullOrWhiteSpace(documentIdentity) &&
            _state.DocumentLanguages.TryGetValue(documentIdentity, out var language) &&
            !string.IsNullOrWhiteSpace(language))
            return language;
        return string.IsNullOrWhiteSpace(_state.ProjectLanguage) ? "en-US" : _state.ProjectLanguage;
    }

    public void SetProjectLanguage(string language)
    {
        _state = _state with { ProjectLanguage = LocalUserDictionary.NormalizeLanguage(language) };
        Persist();
    }

    public void SetDocumentLanguage(string documentIdentity, string? language)
    {
        var languages = new Dictionary<string, string>(_state.DocumentLanguages, StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(language))
            languages.Remove(documentIdentity);
        else
            languages[documentIdentity] = LocalUserDictionary.NormalizeLanguage(language);
        _state = _state with { DocumentLanguages = languages };
        Persist();
    }

    private static SpellingSettingsState Load(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot)) return SpellingSettingsState.Default;
        try
        {
            var path = SettingsPath(projectRoot);
            if (!File.Exists(path)) return SpellingSettingsState.Default;
            var state = JsonSerializer.Deserialize<SpellingSettingsState>(File.ReadAllText(path), JsonOptions);
            if (state is null) return SpellingSettingsState.Default;
            return state with
            {
                ProjectLanguage = LocalUserDictionary.NormalizeLanguage(state.ProjectLanguage),
                DocumentLanguages = new Dictionary<string, string>(
                    state.DocumentLanguages ?? new Dictionary<string, string>(),
                    StringComparer.Ordinal)
            };
        }
        catch
        {
            return SpellingSettingsState.Default;
        }
    }

    private void Persist()
    {
        if (string.IsNullOrWhiteSpace(_projectRoot)) return;
        try
        {
            var path = SettingsPath(_projectRoot);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // Project language settings are helpful metadata, never a reason to interrupt authoring.
        }
    }

    private static string SettingsPath(string projectRoot)
        => Path.Combine(projectRoot, ".typescribe", "spelling.json");

    private sealed record SpellingSettingsState(
        int Version,
        string ProjectLanguage,
        Dictionary<string, string> DocumentLanguages)
    {
        public static SpellingSettingsState Default { get; } =
            new(1, "en-US", new Dictionary<string, string>(StringComparer.Ordinal));
    }
}
