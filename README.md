# Typescribe

Typescribe is a local-first, cross-platform C# desktop application for long-form writing and professional typesetting. Manuscripts stay in plain UTF-8 files, are parsed into a canonical semantic AST, and are published through a LaTeX-only production pipeline.

## Technology

- **.NET 10 / C# 14**
- **Avalonia 12.1.3** for Windows, macOS, and Linux
- **Native AOT** for self-contained platform-native application releases
- **LuaLaTeX** for preview and production PDF generation
- **TinyTeX 2026.09** as the verified portable LuaLaTeX runtime when a system LuaLaTeX installation is unavailable
- **PDFtoImage 5.4.0 + PDFium/SkiaSharp** for cross-platform in-app PDF page rendering
- **Plain UTF-8 manuscript files** plus small text metadata/style files

WPF is intentionally not used because it is Windows-only.

## Desktop writing workspace

Typescribe uses a resizable three-pane writing workspace designed for long-form projects:

- **Binder / Search** on the left
- **Source editor** in the center
- **PDF Preview / Outline** inspector on the right

A desktop menu bar provides File, Edit, View, Insert, Format, Project, Publish, and Help menus. A compact toolbar keeps common actions such as New, Open, Save, Add Chapter, Search, Book Style, Preview, and Publish immediately available. Binder and inspector panes can be hidden when a distraction-reduced writing layout is preferred.

Common shortcuts include save, open, new project, project search, bold, italic, and binder rename. Binder actions are also available from a context menu.

## Implemented v0.1 workflows

### Binder

The binder is backed by the manuscript filesystem and a small `.typescribe/binder.tsv` metadata file. It supports chapters, parts, folders, nested creation based on the selected container, rename, delete, include/exclude from compilation, persistent ordering/display titles, and drag/drop reorganization.

Drag/drop changes the actual project structure rather than only rearranging the screen:

- Drop near the top of an item to move **before** it.
- Drop near the bottom to move **after** it.
- Drop in the middle of a folder or part to move **inside** it.
- Moving a folder reparents its full subtree and updates project-relative paths.
- Cyclic moves, such as moving a folder into one of its descendants, are rejected.

The manuscript files remain normal files under `manuscript/` and can be edited outside Typescribe.

### Markdown-like authoring

The semantic parser supports headings, paragraphs, quotes, ordered/unordered lists, fenced code blocks, thematic breaks, display math, strong/emphasis, inline code, links, and inline math.

````text
# Heading
## Subheading

Normal paragraph with **strong**, *emphasis*, `inline code`,
[a link](https://example.com), and $E = mc^2$ inline math.

> Block quote

- Unordered list item
1. Ordered list item

---

```language
code block
```

$$
F(x) = \int_0^x f(t) dt
$$
````

These structures are represented in the Typescribe AST rather than passed directly to LuaLaTeX.

The Insert and Format menus provide quick authoring commands for headings, strong text, emphasis, inline code, quotes, and bullet lists while still storing plain Markdown-like source.

### Outline and selection-aware preview

When a manuscript document is selected, Typescribe builds an outline from its semantic heading nodes. Selecting an outline heading moves the editor caret to that source line and changes the live preview scope to that section.

The live preview scope follows the writing context:

1. Selecting a chapter/document previews that document by default.
2. Selecting a heading previews that heading and its section.
3. **Show Full Document** clears heading focus.
4. **Whole book** switches the live preview to the complete compilation.
5. Final **Publish PDF** always publishes the complete included book, regardless of the temporary preview scope.

This keeps preview recompilation focused and responsive while still allowing whole-book layout checks when needed.

### Search

Project search covers document titles and manuscript text and supports case-insensitive search by default, case-sensitive matching, whole-word matching, regular expressions, regex timeout protection, and double-click navigation to matching documents and lines.

### Basic book styles

`styles/book.style` stores presentation settings separately from manuscript content. The style editor exposes page width/height, top/bottom/inner/outer margins, body font family, body font size, line spacing, paragraph indentation/spacing, and body justification.

The LaTeX generator consumes this style model for both live preview and final publishing.

### Realtime PDF preview

The inspector contains a real PDF preview, not a semantic-text approximation.

While editing:

1. Typescribe waits about 550 ms after the latest keystroke.
2. Any older preview compilation is cancelled.
3. The current in-memory editor buffer is compiled with LuaLaTeX without waiting for autosave.
4. A fast one-pass build writes `build/live-preview.pdf`.
5. PDFium renders the current page inside Typescribe.

The preview includes page navigation and zoom controls. Style changes, binder ordering, include/exclude changes, document selection, and heading selection also trigger a new scoped preview build.

### PDF publishing

Production PDF output follows:

```text
Manuscript files / editor buffer
    ↓
Typescribe parser
    ↓
Semantic AST
    ↓
Book style
    ↓
LaTeX generator
    ↓
LuaLaTeX
    ↓
PDF
```

Live preview uses one LuaLaTeX pass for responsiveness. **Publish PDF** uses two passes for stable references/page numbering and writes to the user-selected destination.

Typescribe resolves LuaLaTeX in this order:

1. `TYPESCRIBE_LUALATEX` environment variable
2. A Typescribe-managed TinyTeX runtime
3. `lualatex` on the system `PATH`

If no engine is available, **Download LuaLaTeX** downloads a pinned full TinyTeX 2026.09 archive for the current platform, verifies its SHA-256 digest, installs it under the user's local application-data directory, and continues without a separate manual TeX installation.

LuaLaTeX is launched with shell escape disabled. Preview compilation is cancellable so stale background compiler processes do not continue after new edits.

## Other implemented foundation features

- Create/open local projects
- Source editor with debounced autosave (~850 ms)
- Atomic file replacement
- Unicode-aware word counting
- Whole-book compilation in binder order
- LaTeX source export
- Explicit AOT-friendly composition root
- Project-relative path containment checks
- Cross-platform CI builds
- Native AOT release scripts

## Architecture

```text
Typescribe.Desktop
        │  Avalonia UI + PDFium preview
        ├───────────────┐
        ▼               ▼
Typescribe.Application  Typescribe.Infrastructure
        │               filesystem, atomic saves,
        ▼               LuaLaTeX adapter
Typescribe.Domain
binder tree + semantic AST + styles
```

Dependencies point inward. `Domain` is framework-free. `Application` defines use cases and ports. `Infrastructure` implements persistence and publishing. `Desktop` is the composition root and UI layer.

## Project format

```text
my-book/
  typescribe.yaml
  .typescribe/
    binder.tsv
  manuscript/
    chapter-01.md
  research/
  assets/
  styles/
    book.style
  build/
    live-preview.pdf
```

Generated `.tex` and `.pdf` files are outputs, not canonical manuscript content.

## Development

Developer prerequisite: .NET 10 SDK.

```bash
dotnet restore Typescribe.slnx
dotnet run --project src/Typescribe.Desktop/Typescribe.Desktop.csproj
```

A TeX installation is not required just to build or open the editor. PDF preview/publishing can install the verified portable LuaLaTeX runtime from inside Typescribe when first needed.

## Portable Native AOT releases

### Windows x64

```powershell
./scripts/publish.ps1 -Rid win-x64
```

### Linux

```bash
./scripts/publish.sh linux-x64
# or
./scripts/publish.sh linux-arm64
```

### macOS

```bash
./scripts/publish.sh osx-arm64
# or
./scripts/publish.sh osx-x64
```

Native AOT output is operating-system/architecture-specific. End users do not need the .NET runtime. If LuaLaTeX is unavailable, Typescribe can fetch its verified TinyTeX runtime on first PDF use.

## Current limitations

This is still a v0.1 implementation, not the entire product specification. Remaining larger gaps include persistent workspace-layout preferences, rich-text WYSIWYM editing, precise cursor-to-PDF synchronization, virtualized multi-page PDF scrolling/thumbnails, figures/assets in the AST, tables, footnotes, citations/bibliography, cross-references, corkboard, rich document metadata/inspector fields, comments/track changes, EPUB/DOCX export, snapshots, plugin sandboxing, undoable filesystem binder operations, and structured compiler diagnostics mapped to manuscript source lines.

## Security and privacy defaults

Typescribe keeps manuscript work local. Project-relative paths are canonicalized before file access, saves use atomic replacement, binder drag/drop rejects cyclic tree moves, publishing uses generated LaTeX rather than manuscript-provided shell commands, LuaLaTeX runs with `-no-shell-escape`, and downloaded TinyTeX archives are checksum-verified. No manuscript telemetry or upload path is present in the v0.1 foundation.

## Repository notes

No source-code license has been selected for Typescribe yet. Review `THIRD_PARTY_NOTICES.md` before distribution.
