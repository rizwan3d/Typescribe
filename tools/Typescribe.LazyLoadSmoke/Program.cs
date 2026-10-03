using Typescribe.Application.Abstractions;
using Typescribe.Application.Services;
using Typescribe.Desktop;
using Typescribe.Desktop.ViewModels;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

var root = Path.Combine(Path.GetTempPath(), "typescribe-lazy-smoke-" + Guid.NewGuid().ToString("N"));
var protectedFiles = new List<string>();

try
{
    var filesystem = new FileSystemProjectRepository();
    await filesystem.CreateAsync(root, "Lazy Loading Smoke");

    var manuscript = Path.Combine(root, "manuscript");
    const int extraDocuments = 120;
    var payload = new string('x', 96 * 1024);
    for (var index = 1; index <= extraDocuments; index++)
    {
        var path = Path.Combine(manuscript, $"scene-{index:000}.md");
        await File.WriteAllTextAsync(path, $"# Scene {index}\n\n{payload}\n");
        protectedFiles.Add(path);
        MakeUnreadable(path);
    }

    var countingRepository = new CountingRepository(filesystem);
    var parser = new AdvancedDocumentParser();
    var viewModel = new WorkspaceViewModel(
        countingRepository,
        parser,
        new DocumentRenderer(),
        new WordCountService(),
        new ProjectSearchService(countingRepository),
        new DisabledExportService());

    await using (viewModel)
    {
        await viewModel.OpenProjectAsync(root);

        Require(countingRepository.DocumentReads == 1,
            $"Project startup read {countingRepository.DocumentReads} manuscripts; expected exactly the current document.");
        Require(viewModel.BinderRows.Count(row => row.Node.IsDocument) == extraDocuments + 1,
            "Binder metadata did not load the full project structure.");
        Require(viewModel.CorkboardCards.Count == extraDocuments + 1,
            "Visible Corkboard metadata was not available after startup.");

        // This coordinator used to rebuild writing statistics by reading every manuscript.
        // The unreadable scene files make any regression in that path fail immediately.
        var authoring = new AuthoringFeatureCoordinator();
        await authoring.OpenAsync(root, viewModel.BinderRows);

        var next = viewModel.BinderRows.First(row =>
            row.Node.RelativePath?.EndsWith("scene-001.md", StringComparison.Ordinal) == true);
        MakeReadable(Path.Combine(root, next.Node.RelativePath!.Replace('/', Path.DirectorySeparatorChar)));
        await viewModel.SelectAsync(next);

        Require(countingRepository.DocumentReads == 2,
            $"Selecting one additional manuscript produced {countingRepository.DocumentReads} total reads; expected two.");
        Require(viewModel.EditorText.StartsWith("# Scene 1", StringComparison.Ordinal),
            "The lazily selected document did not load correctly.");
    }

    Console.WriteLine($"TS-223 lazy-loading smoke passed: {extraDocuments + 1} documents, one startup manuscript read.");
    return 0;
}
finally
{
    foreach (var path in protectedFiles)
        MakeReadable(path);
    try
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
    catch
    {
        // CI temp cleanup is best effort; test failures should report the actual invariant violation.
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void MakeUnreadable(string path)
{
    if (OperatingSystem.IsWindows()) return;
    File.SetUnixFileMode(path, UnixFileMode.None);
}

static void MakeReadable(string path)
{
    if (!File.Exists(path) || OperatingSystem.IsWindows()) return;
    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}

internal sealed class CountingRepository(IProjectRepository inner) : IProjectRepository
{
    public int DocumentReads { get; private set; }

    public Task<BookProject> CreateAsync(string rootPath, string title, CancellationToken cancellationToken = default)
        => inner.CreateAsync(rootPath, title, cancellationToken);

    public Task<BookProject> OpenAsync(string rootPath, CancellationToken cancellationToken = default)
        => inner.OpenAsync(rootPath, cancellationToken);

    public Task<string> ReadDocumentAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
    {
        DocumentReads++;
        return inner.ReadDocumentAsync(project, node, cancellationToken);
    }

    public Task SaveDocumentAsync(BookProject project, ProjectNode node, string content, CancellationToken cancellationToken = default)
        => inner.SaveDocumentAsync(project, node, content, cancellationToken);

    public Task SaveDocumentStatisticsAsync(BookProject project, ProjectNode node, int wordCount, CancellationToken cancellationToken = default)
        => inner.SaveDocumentStatisticsAsync(project, node, wordCount, cancellationToken);

    public Task<ProjectNode> AddChapterAsync(BookProject project, string title, CancellationToken cancellationToken = default)
        => inner.AddChapterAsync(project, title, cancellationToken);

    public Task<ProjectNode> AddNodeAsync(BookProject project, ProjectNode? parent, NodeKind kind, string title, CancellationToken cancellationToken = default)
        => inner.AddNodeAsync(project, parent, kind, title, cancellationToken);

    public Task RenameNodeAsync(BookProject project, ProjectNode node, string newTitle, CancellationToken cancellationToken = default)
        => inner.RenameNodeAsync(project, node, newTitle, cancellationToken);

    public Task DeleteNodeAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => inner.DeleteNodeAsync(project, node, cancellationToken);

    public Task<bool> MoveNodeAsync(BookProject project, ProjectNode node, int offset, CancellationToken cancellationToken = default)
        => inner.MoveNodeAsync(project, node, offset, cancellationToken);

    public Task<bool> ReparentNodeAsync(BookProject project, ProjectNode node, ProjectNode? newParent, int targetIndex, CancellationToken cancellationToken = default)
        => inner.ReparentNodeAsync(project, node, newParent, targetIndex, cancellationToken);

    public Task SetCompilationIncludedAsync(BookProject project, ProjectNode node, bool included, CancellationToken cancellationToken = default)
        => inner.SetCompilationIncludedAsync(project, node, included, cancellationToken);

    public Task SaveNodeMetadataAsync(BookProject project, ProjectNode node, string synopsis, string notes, string status, string label, string keywords, int targetWords, CancellationToken cancellationToken = default)
        => inner.SaveNodeMetadataAsync(project, node, synopsis, notes, status, label, keywords, targetWords, cancellationToken);

    public Task<SnapshotInfo> CreateSnapshotAsync(BookProject project, ProjectNode node, string content, string label, CancellationToken cancellationToken = default)
        => inner.CreateSnapshotAsync(project, node, content, label, cancellationToken);

    public Task<IReadOnlyList<SnapshotInfo>> ListSnapshotsAsync(BookProject project, ProjectNode node, CancellationToken cancellationToken = default)
        => inner.ListSnapshotsAsync(project, node, cancellationToken);

    public Task<string> ReadSnapshotAsync(BookProject project, ProjectNode node, SnapshotInfo snapshot, CancellationToken cancellationToken = default)
        => inner.ReadSnapshotAsync(project, node, snapshot, cancellationToken);

    public Task DeleteSnapshotAsync(BookProject project, ProjectNode node, SnapshotInfo snapshot, CancellationToken cancellationToken = default)
        => inner.DeleteSnapshotAsync(project, node, snapshot, cancellationToken);

    public Task SaveStyleAsync(BookProject project, BookStyle style, CancellationToken cancellationToken = default)
        => inner.SaveStyleAsync(project, style, cancellationToken);

    public IAsyncEnumerable<(ProjectNode Node, string Content)> EnumerateDocumentsAsync(BookProject project, CancellationToken cancellationToken = default)
        => inner.EnumerateDocumentsAsync(project, cancellationToken);
}

internal sealed class DisabledExportService : IDocumentExportService
{
    public bool CanPublishPdf => false;
    public string PublishingEngineName => "Disabled for lazy-loading smoke";

    public Task EnsurePdfEngineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ExportPdfPreviewAsync(string source, string title, BookStyle style, string destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ExportPdfAsync(string source, string title, BookStyle style, string destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task ExportLatexAsync(string source, string title, BookStyle style, string destination, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
