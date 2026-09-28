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

## Authoring workspace

Typescribe now uses a long-form authoring workspace inspired by the workflow of established writing applications while keeping its own UI and project format.

The main shell provides:

- **Binder / Search** on the left
- **Editor / Corkboard** in the center
- **Inspector / PDF / Outline / Snapshots** on the right
- Resizable and hideable side panes
- Desktop menu bar, command toolbar, context menus, and keyboard shortcuts
- **F11 Composition Mode** for distraction-reduced fullscreen writing

Selecting a folder or part automatically brings the Corkboard forward. Selecting a manuscript document brings the Editor forward.

## Binder

The binder is backed by the manuscript filesystem and `.typescribe/binder.tsv`. It supports chapters, parts, folders, nested creation, rename, delete, include/exclude, persistent ordering, and drag/drop reorganization.

Drag/drop changes the actual project structure:

- Drop near the top of an item to move **before** it.
- Drop near the bottom to move **after** it.
- Drop in the middle of a folder or part to move **inside** it.
- Moving a folder reparents its full subtree and updates project-relative paths.
- Cyclic moves are rejected.

Each binder item also has a stable persistent ID independent from its filesystem path. Metadata and snapshots therefore remain associated with a document after rename or reparenting.

## Corkboard

The center Corkboard shows index-card-style planning cards for the selected container, or the siblings of the selected document.

Cards show:

- Title
- Synopsis
- Document kind
- Compile state
- Status
- Label
- Current word count
- Word-target progress when a target is configured

Double-clicking a card opens that binder item. Container cards keep the Corkboard visible; document cards switch to the Editor.

## Inspector and document metadata

The Inspector persists planning metadata separately from manuscript prose:

- Synopsis
- Notes
- Status
- Label
- Keywords
- Word target

Inspector fields autosave after a short idle period and are flushed before document selection changes. Metadata is stored in the project’s text-based binder metadata rather than embedded in manuscript files.

The current document target is shown as a progress bar and in the status area while writing.

## Snapshots

Each manuscript document can have immutable timestamped snapshots stored under `.typescribe/snapshots/`.

- **Take Snapshot** captures the current in-memory editor text, including unsaved edits.
- The Snapshots inspector lists date/time, word count, and label.
- **Restore** replaces the current editor buffer with the selected snapshot.
- Before restore, Typescribe automatically creates a safety snapshot of the current text so the operation remains reversible.
- Snapshots can be deleted explicitly.

## Composition Mode

Press **F11** or use **View → Composition Mode** to enter distraction-reduced writing mode.

Composition Mode:

- Hides Binder and Inspector
- Hides menus, toolbar, and status chrome
- Expands the editor
- Uses fullscreen window mode
- Keeps autosave and live document state active

Press F11 again to restore the previous window and pane state.

## Markdown-like authoring

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

## Outline and selection-aware preview

When a manuscript document is selected, Typescribe builds an outline from semantic heading nodes. Selecting a heading moves the editor caret to its source line and changes live preview scope to that section.

Preview scope follows writing context:

1. Selecting a chapter/document previews that document by default.
2. Selecting a heading previews that heading and its section.
3. **Show Full Document** clears heading focus.
4. **Whole book** switches live preview to the complete compilation.
5. Final **Publish PDF** always publishes the complete included book.

## Search

Project search covers document titles and manuscript text and supports:

- Case-insensitive search by default
- Case-sensitive matching
- Whole-word matching
- Regular expressions
- Regex timeout protection
- Double-click navigation to matching documents and source lines

## Book styles

`styles/book.style` keeps presentation separate from manuscript content. The current style editor exposes page dimensions, margins, body font, font size, line spacing, paragraph indentation/spacing, and justification.

The LaTeX generator consumes the same style model for live preview and final publishing.

## Realtime PDF preview

The PDF inspector is a real generated PDF preview.

While editing:

1. Typescribe waits about 550 ms after the latest keystroke.
2. An older preview compilation is cancelled.
3. The current in-memory editor buffer is compiled with LuaLaTeX without waiting for autosave.
4. A one-pass preview writes `build/live-preview.pdf`.
5. PDFium renders the current page inside Typescribe.

The preview supports page navigation and zoom. Selection, heading focus, style changes, binder ordering, and compile inclusion changes can trigger a fresh scoped preview.

## PDF publishing

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

Live preview uses one LuaLaTeX pass for responsiveness. **Publish PDF** uses two passes for stable references/page numbering.

Typescribe resolves LuaLaTeX in this order:

1. `TYPESCRIBE_LUALATEX`
2. Typescribe-managed TinyTeX runtime
3. `lualatex` on `PATH`

If unavailable, Typescribe can download a pinned full TinyTeX 2026.09 archive, verify its SHA-256 digest, install it in local application data, and continue without a separate manual TeX installation.

LuaLaTeX is launched with shell escape disabled.

## Project format

```text
my-book/
  typescribe.yaml
  .typescribe/
    binder.tsv
    snapshots/
      <persistent-document-id>/
        <snapshot-id>.md
        <snapshot-id>.meta
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

Native AOT output is operating-system/architecture-specific. End users do not need the .NET runtime.

## Current limitations

Typescribe is still an early implementation. Larger remaining gaps include persisted workspace-layout preferences, Corkboard drag/drop directly on cards, a spreadsheet-style project Outliner, project/session/daily writing targets, snapshot compare/diff, rich-text WYSIWYM editing, precise cursor-to-PDF synchronization, virtualized multi-page PDF scrolling/thumbnails, figures/assets in the AST, tables, footnotes, citations/bibliography, cross-references, comments/track changes, EPUB/DOCX export, undoable filesystem binder operations, plugin sandboxing, and structured compiler diagnostics mapped to manuscript source lines.

## Security and privacy defaults

Typescribe keeps manuscript work local. Project-relative paths are canonicalized before file access, saves use atomic replacement, binder drag/drop rejects cyclic tree moves, publishing uses generated LaTeX rather than manuscript-provided shell commands, LuaLaTeX runs with `-no-shell-escape`, and downloaded TinyTeX archives are checksum-verified. No manuscript telemetry or upload path is present.

## Repository notes

No source-code license has been selected for Typescribe yet. Review `THIRD_PARTY_NOTICES.md` before distribution.
