using Typescribe.Application.Services;
using Typescribe.Domain.Models;
using Typescribe.Infrastructure.Services;

var destination = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.Combine(Path.GetTempPath(), "typescribe-epub-smoke.epub");
var root = Path.Combine(Path.GetTempPath(), "typescribe-epub-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(root, "assets"));
Directory.CreateDirectory(Path.Combine(root, "manuscript"));

try
{
    await File.WriteAllTextAsync(
        Path.Combine(root, "assets", "cover.svg"),
        """
        <svg xmlns="http://www.w3.org/2000/svg" width="600" height="900" viewBox="0 0 600 900">
          <rect width="600" height="900" fill="#f4f1e8"/>
          <text x="300" y="430" text-anchor="middle" font-size="48">TypeScribe</text>
          <text x="300" y="500" text-anchor="middle" font-size="28">EPUB smoke test</text>
        </svg>
        """);

    var projectRoot = new ProjectNode("root", "EPUB Smoke Book", NodeKind.Book, persistentId: "root");
    var chapter = new ProjectNode("manuscript/chapter-01.md", "A Semantic Chapter", NodeKind.Chapter, "manuscript/chapter-01.md", "chapter-one");
    projectRoot.AddChild(chapter);
    var project = new BookProject
    {
        RootPath = root,
        Title = "EPUB Smoke Book",
        Author = "TypeScribe",
        Language = "en",
        Root = projectRoot
    };

    const string manuscript = """
        # A Semantic Chapter {#chapter-semantic}

        This paragraph has **strong text**, *emphasis*, [an external link](https://example.com), a footnote[^note], and a reference to [@ref:fig-cover].

        ![Smoke-test cover](assets/cover.svg) {#fig-cover}

        - First item
        - Second item

        | Name | Value |
        | :--- | ---: |
        | Alpha | 1 |
        | Beta | 2 |

        [^note]: This is a semantic EPUB footnote.
        """;

    var exporter = new Epub3ExportService(new AdvancedDocumentParser());
    await exporter.ExportAsync(project, [new EpubDocumentSource(chapter, manuscript)], destination);
    Console.WriteLine(destination);
}
finally
{
    try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    catch { }
}
