# Typescribe

Typescribe is a local-first, cross-platform C# desktop application for long-form writing and professional typesetting. It keeps manuscripts in plain UTF-8 files, parses them into a canonical semantic AST, and keeps authoring independent from the production typesetting backend.

## Technology

- **.NET 10 / C# 14**
- **Avalonia 12.1.3** for Windows, macOS, and Linux
- **Native AOT** for self-contained platform-native application releases
- **LuaLaTeX** for production PDF generation
- **TinyTeX 2026.09** as the verified portable LuaLaTeX runtime when a system LuaLaTeX installation is unavailable
- **Typst source export** retained as an alternate generated format, not as the canonical manuscript
- **Plain UTF-8 manuscript files** plus small text metadata/style files

WPF is intentionally not used because it is Windows-only.

## Implemented v0.1 workflows

### Binder

The binder is backed by the manuscript filesystem and a small `.typescribe/binder.tsv` metadata file. It supports:

- Chapters
- Parts
- Folders
- Nested creation based on the selected container
- Rename
- Delete
- Move up/down
- Include/exclude from compilation
- Persistent binder order and display titles

The manuscript files remain normal files under `manuscript/` and can be edited outside Typescribe.

### Markdown-like authoring

The current semantic parser supports:

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

These structures are represented in the Typescribe AST rather than being passed directly to a publishing engine.

### Search

Project search covers document titles and manuscript text and supports:

- Case-insensitive search by default
- Case-sensitive search
- Whole-word matching
- Regular expressions
- Regex timeout protection
- Double-click navigation to matching documents

### Basic book styles

`styles/book.style` stores presentation settings separately from manuscript content. The current style editor exposes:

- Page width and height
- Top, bottom, inner, and outer margins
- Body font family
- Body font size
- Line spacing
- Paragraph indentation
- Paragraph spacing
- Body justification

The same style model is consumed by generated LaTeX and Typst output.

### PDF preview and publishing

Production PDF output follows:

```text
Manuscript files
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

**Preview PDF** compiles the current book to `build/preview.pdf` and opens it using the operating system's PDF viewer. **Publish Book PDF** compiles the same production pipeline to a user-selected destination.

Typescribe first looks for:

1. `TYPESCRIBE_LUALATEX` environment variable
2. A previously downloaded Typescribe-managed TinyTeX runtime
3. `lualatex` on the system `PATH`

If no engine is available, **Download LuaLaTeX** (or Preview/Publish itself) downloads a pinned TinyTeX release for the current platform, verifies its SHA-256 digest, installs it under the user's local application-data directory, and continues without requiring a separate manual TeX installation.

LuaLaTeX is started with shell escape disabled.

## Other implemented foundation features

- Create/open local projects
- Chapter source editor
- Semantic live text preview while writing
- Debounced autosave (~850 ms)
- Atomic file replacement
- Unicode-aware word counting
- Whole-book compilation in binder order
- LaTeX source export
- Typst source export
- Explicit AOT-friendly composition root
- Project-relative path containment checks
- Cross-platform CI builds
- Native AOT release scripts

## Architecture

```text
Typescribe.Desktop
        │
        ├───────────────┐
        ▼               ▼
Typescribe.Application  Typescribe.Infrastructure
        │               filesystem, atomic saves,
        ▼               LuaLaTeX adapter
Typescribe.Domain
project tree + semantic AST + styles
```

Dependencies point inward. `Domain` is framework-free. `Application` defines use cases and ports. `Infrastructure` implements persistence and publishing. `Desktop` is the composition root and UI layer.

Patterns used include Repository, Strategy/Adapter, Ports & Adapters, Presentation Model, Composition Root, debounce, and atomic replace.

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
    preview.pdf
```

Generated `.typ`, `.tex`, and `.pdf` files are outputs, not canonical manuscript content.

## Development

Developer prerequisite: .NET 10 SDK.

```bash
dotnet restore Typescribe.slnx
dotnet run --project src/Typescribe.Desktop/Typescribe.Desktop.csproj
```

A TeX installation is not required just to build or run the editor. PDF preview/publishing can install the verified portable LuaLaTeX runtime from inside Typescribe when first needed.

## Portable Native AOT releases

### Windows x64

```powershell
./scripts/publish.ps1 -Rid win-x64
```

Output: `artifacts/Typescribe-win-x64.zip`

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

Native AOT output is operating-system/architecture-specific. End users do not need to install the .NET runtime. If LuaLaTeX is not already available, Typescribe can fetch its verified portable TinyTeX runtime on first PDF use.

## Current limitations

This is still a v0.1 implementation, not the entire product specification. Not yet implemented include drag/drop binder reparenting, rich-text WYSIWYM editing, figures/assets in the AST, tables, footnotes, citations/bibliography, cross-references, corkboard, outline, comments/track changes, EPUB/DOCX export, snapshots, plugin sandboxing, and an embedded in-app paginated PDF canvas. Production PDF preview currently opens the generated PDF in the operating system viewer.

## Security and privacy defaults

Typescribe keeps manuscript work local. Project-relative paths are canonicalized before file access, saves use atomic replacement, publishing uses generated LaTeX rather than manuscript-provided shell commands, and LuaLaTeX runs with `-no-shell-escape`. No manuscript telemetry or upload path is present in the v0.1 foundation.

## Repository notes

No source-code license has been selected for Typescribe yet. Review `THIRD_PARTY_NOTICES.md` before distribution.
