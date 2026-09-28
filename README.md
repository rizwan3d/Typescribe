# Typescribe

Typescribe is a local-first, cross-platform C# desktop application for long-form writing and professional typesetting. Manuscripts stay in plain UTF-8 files, planning metadata stays in small project sidecars, and publication uses a LaTeX-only pipeline.

## Technology

- **.NET 10 / C# 14**
- **Avalonia 12.1.3** for Windows, macOS, and Linux
- **Native AOT** for self-contained platform-native releases
- **LuaLaTeX** for preview and production PDF generation
- **TinyTeX 2026.09** as the verified portable LuaLaTeX runtime when needed
- **PDFtoImage + PDFium/SkiaSharp** for in-app PDF rendering
- **Plain UTF-8 manuscript files** with text-based project metadata

WPF is intentionally not used because it is Windows-only.

## Studio workspace

Typescribe uses a multi-pane long-form writing studio inspired by established authoring workflows while keeping its own UI and project format.

- **Left:** Binder, Search, Collections
- **Center:** Editor, Corkboard, Outliner
- **Right:** Inspector, Comments, PDF, Outline, Snapshots, Project Targets
- Resizable and hideable side panes
- Menu bar, command toolbar, context menus, and keyboard shortcuts
- **F11 Composition Mode** for fullscreen distraction-reduced writing

Selecting a folder or part favors planning views; selecting a manuscript document favors the Editor.

## Binder

The Binder is backed by the real manuscript filesystem plus `.typescribe/binder.tsv`.

It supports chapters, parts, folders, nested creation, rename, delete, include/exclude, persistent ordering, move up/down, and drag/drop reorganization. Drag/drop performs real filesystem/tree moves: drop above/below to reorder or into a folder/part to reparent. Cyclic moves are rejected.

Each binder item has a stable persistent ID independent of its path, so snapshots, comments, custom metadata, and collections survive rename and drag/drop moves.

## Corkboard

The Corkboard shows index-card planning cards for the active binder group. Cards display title, synopsis, status, label, compile state, word count, and document-target progress.

Cards can be double-clicked to open the item and can now be **dragged directly on the Corkboard to reorder siblings**. The Corkboard reuses the same binder move operation as the Binder, so planning order and compile order remain consistent.

## Project Outliner

The **Outliner** is a spreadsheet-style project view. Its built-in columns include:

- Title
- Type
- Status
- Label
- Current words
- Word target
- Compile inclusion

Every project-defined custom metadata field automatically becomes an additional Outliner column. Clicking a document title opens it in the Editor. Manual Collections can filter the Outliner to a curated set of documents.

## Inspector and custom metadata

The Inspector keeps planning information separate from manuscript prose:

- Synopsis
- Notes
- Status
- Label
- Keywords
- Per-document word target
- User-defined custom metadata fields

Standard Inspector fields autosave after a short idle period. Custom metadata fields are persisted when edited and are also visible as Outliner columns.

Custom field definitions, values, writing targets, comments, and collections are stored in `.typescribe/authoring.tsv` rather than embedded in manuscript files.

## Comments and annotations

Document comments are line-anchored annotations stored outside manuscript text.

- **Document → Add Comment at Caret** creates a comment at the current source line.
- The Comments inspector lists open and resolved comments.
- Double-clicking a comment jumps the editor to its source line.
- Comments can be resolved/reopened or deleted.

Line anchors are intentionally lightweight in this version; edits above an annotation do not yet automatically remap its line number.

## Writing targets

Typescribe supports four levels of writing progress:

- Per-document target in the Inspector
- **Project target** for total manuscript words
- **Daily target** measured from the first project word count seen on the current local day
- **Session target** measured from the project word count when the project is opened

The Project inspector shows independent progress bars for project, daily, and session targets. Daily baseline/date are persisted across restarts; session baseline resets when the project is opened. Live editor changes update target progress without waiting for autosave.

## Snapshots and diff comparison

Each manuscript document can have immutable timestamped snapshots under `.typescribe/snapshots/`.

- **Take Snapshot** captures the current in-memory text, including unsaved edits.
- Snapshot entries show date/time, word count, and label.
- **Compare** opens a line-oriented snapshot-vs-current diff with added/removed/unchanged lines.
- **Restore** replaces the editor buffer with the selected snapshot.
- Before restore, Typescribe automatically creates a safety snapshot of the current text.
- Snapshots can be deleted explicitly.

The diff service uses a bounded line LCS for normal documents and a memory-safe fallback for unusually large files.

## Collections and saved searches

The left Collections tab supports two collection types:

### Saved searches

A current project search can be saved with its query and matching options (case, whole word, regex). Double-clicking the collection reruns that search.

### Manual collections

A manual collection stores stable binder-item IDs rather than paths. Create one from the selected binder item, add additional selections later, and double-click it to filter the Outliner to that collection.

Collections are organizational views only; they do not duplicate or move manuscript files.

## Project templates

Use **Project → Save Project as Template** to save the current project structure as a reusable local template. Runtime build output and document snapshots are excluded.

Use **File → New from Template** to choose a saved template, select an empty destination folder, enter a new project title, materialize the project, and open it immediately.

Templates are stored in the user's local Typescribe application-data directory, not inside the active manuscript project.

## Composition Mode

Press **F11** or use **View → Composition Mode**.

Composition Mode hides Binder, Inspector, menus, toolbar, and status chrome; expands the Editor; and enters fullscreen mode while preserving autosave and live document state. Press F11 again to restore the previous pane visibility and window state.

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

These structures are parsed into the Typescribe AST rather than passed directly to LuaLaTeX.

## Outline and selection-aware preview

Selecting a manuscript document builds a heading Outline. Selecting a heading moves the editor caret to its source line and scopes live PDF preview to that section. Clear heading focus to preview the whole document, or enable **Whole book** to preview the complete compilation.

Final **Publish PDF** always publishes the complete included book regardless of temporary preview scope.

## Search

Project search covers document titles and manuscript text and supports case-insensitive/default matching, case-sensitive matching, whole-word matching, regular expressions, regex timeout protection, and navigation to matching documents/source lines.

Searches can be saved as Collections.

## Book styles

`styles/book.style` keeps presentation separate from manuscript content. The style editor exposes page dimensions, margins, body font, font size, line spacing, paragraph indentation/spacing, and justification. The same style model feeds live preview and final publishing.

## Realtime PDF preview and publishing

The PDF inspector is a real generated PDF preview:

1. Editor input is debounced.
2. Obsolete preview compilation is cancelled.
3. Current in-memory manuscript text is converted from semantic AST to LaTeX.
4. LuaLaTeX performs a fast one-pass preview build.
5. PDFium renders the requested page in Typescribe.

Final **Publish PDF** uses two LuaLaTeX passes for stable references/page numbering.

Typescribe resolves LuaLaTeX from `TYPESCRIBE_LUALATEX`, then its managed TinyTeX runtime, then system `PATH`. If unavailable, the application can download and SHA-256 verify a pinned full TinyTeX runtime. LuaLaTeX runs with shell escape disabled.

## Project format

```text
my-book/
  typescribe.yaml
  .typescribe/
    binder.tsv
    authoring.tsv
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

Canonical writing remains in manuscript files. `.typescribe/` contains Typescribe-specific planning state. Generated `.tex` and `.pdf` files are outputs.

## Development

Developer prerequisite: .NET 10 SDK.

```bash
dotnet restore Typescribe.slnx
dotnet run --project src/Typescribe.Desktop/Typescribe.Desktop.csproj
```

A TeX installation is not required just to build/open the editor. PDF features can install the verified portable LuaLaTeX runtime on first use.

## Portable Native AOT releases

```powershell
# Windows x64
./scripts/publish.ps1 -Rid win-x64
```

```bash
# Linux
./scripts/publish.sh linux-x64
./scripts/publish.sh linux-arm64

# macOS
./scripts/publish.sh osx-arm64
./scripts/publish.sh osx-x64
```

Native AOT output is OS/architecture-specific; end users do not need the .NET runtime.

## Current limitations

Typescribe is still an early implementation. Remaining larger gaps include persisted workspace-layout preferences, editable/sortable Outliner cells, freeform Corkboard layouts/card stacks, robust annotation anchor remapping after edits, rich-text WYSIWYM editing, precise cursor-to-PDF synchronization, virtualized multi-page PDF scrolling/thumbnails, figures/assets in the AST, tables, footnotes, citations/bibliography, cross-references, track changes, EPUB/DOCX export, undoable filesystem binder operations, plugin sandboxing, and structured LuaLaTeX diagnostics mapped to manuscript source lines.

## Security and privacy defaults

Typescribe keeps manuscript work local. Project-relative paths are canonicalized before access, saves use atomic replacement, binder/Corkboard drag operations reject invalid moves, comments and metadata do not modify manuscript prose, LuaLaTeX runs with `-no-shell-escape`, and downloaded TinyTeX archives are checksum-verified. No manuscript telemetry/upload path is present.

## Repository notes

No source-code license has been selected for Typescribe yet. Review `THIRD_PARTY_NOTICES.md` before distribution.
