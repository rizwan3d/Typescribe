# Typescribe architecture

## Goals

Typescribe is a local-first, cross-platform authoring and typesetting desktop application. Manuscript content stays independent from presentation while the production rendering path is deliberately standardized on LaTeX/LuaLaTeX.

## Layering

```text
Typescribe.Desktop
    │  Avalonia UI + PDFium page preview
    ▼
Typescribe.Application
    │  use cases + ports + parser/render/search services
    ▼
Typescribe.Domain
    │  binder tree + semantic AST + BookStyle
    ▲
    │  port implementations
Typescribe.Infrastructure
       filesystem persistence + LuaLaTeX publishing
```

Domain has no package dependencies. Application depends only on Domain. Infrastructure implements Application ports. Desktop composes concrete implementations and owns PDF page rasterization for presentation.

## Design patterns

- **Repository** — `IProjectRepository` hides project filesystem and binder metadata persistence.
- **Transactional Mutation Service** — `IProjectMutationService` is the single boundary for destructive binder mutations and persists restart-safe undo/recovery records.
- **Strategy / Adapter** — `IPdfPublishingEngine` isolates the LuaLaTeX process adapter.
- **Ports and Adapters** — Application interfaces define the boundaries implemented by Infrastructure.
- **Presentation Model** — `WorkspaceViewModel` owns editor, binder, outline, search, and live-preview state without Avalonia file-picker dependencies.
- **Composition Root** — `App` explicitly creates services and avoids reflection-heavy DI.
- **Debounce** — manuscript changes autosave and compile preview after separate short idle periods.
- **Cancellation** — a newer edit cancels an obsolete preview compilation/process tree.
- **Atomic Replace** — writes use sibling temporary files before replacement.

## AOT decisions

- `net10.0` + Native AOT release configuration.
- No dynamic proxy container or runtime assembly scanning.
- No reflection-based canonical project serializer.
- Code-only Avalonia UI.
- LuaLaTeX executes as an isolated external publishing process rather than being loaded into the application process.
- In-app PDF pages are rendered through PDFtoImage/PDFium/SkiaSharp, keeping the preview cross-platform without embedding a browser control.

## Canonical document model

Markdown-like manuscript source is parsed into `DocumentAst`. LaTeX is generated from this model and is never canonical manuscript content.

Current block nodes:

- Heading
- Paragraph
- Quote
- Ordered/unordered list item
- Code block
- Display math
- Thematic break

Current inline nodes:

- Text
- Strong emphasis
- Emphasis
- Inline code
- Link
- Inline math

The parser can be extended with figures, tables, citations, footnotes, semantic blocks, and cross-references without making authors write LaTeX directly.

Heading nodes also drive the desktop outline. Each outline item retains its source line and computed section end, allowing heading selection to navigate the editor and temporarily narrow live-preview compilation to that section without changing canonical manuscript content.

## Binder model

The physical manuscript hierarchy remains under `manuscript/`. Binder-specific information that does not belong in manuscript text is stored in `.typescribe/binder.tsv`.

The sidecar persists node type, include/exclude state, project-relative path, display title, metadata, persistent document ID, and sibling ordering through record order. Filesystem paths remain the durable ownership boundary while the sidecar provides application-specific organization without introducing a proprietary manuscript database.

Destructive binder changes are routed through `IProjectMutationService`. `TransactionalProjectRepository` decorates the existing filesystem repository so current rename, delete, reorder, and drag/drop/reparent flows automatically use the transaction boundary. Before touching manuscript files, `FileSystemProjectMutationService` writes a prepared transaction to `.typescribe/history/<transaction-id>.json` containing the pre-mutation binder snapshot and filesystem movement information. The transaction is marked complete only after the filesystem change and updated binder sidecar are durable. If a prepared transaction is found when a project is reopened, it is rolled back before the binder is loaded.

Undo and redo operate from those persisted transaction records, so rename, delete, sibling reorder, and cross-container moves remain reversible after restart. A new mutation abandons an outstanding redo stack rather than replaying stale filesystem intent.

Deleting a binder item does not initially destroy its file or directory. The payload moves into `.typescribe/trash/<transaction-id>/payload/` and a companion trash record stores its original path, persistent ID, metadata, parent persistent ID, binder position, and deletion timestamp. **Project → Trash…** can restore the item to its original binder position without changing its persistent ID, or permanently remove it when explicitly requested.

## Working-state recovery

Canonical autosave and crash recovery are intentionally separate mechanisms. A small recovery journal under `.typescribe/recovery/` captures editor buffers that differ from the canonical manuscript:

```text
.typescribe/recovery/
    session.json
    <persistent-document-id>.recovery.md
```

The journal is refreshed on a short interval and removed on a clean shutdown. If a previous-session journal remains, the next project open presents **Restore All**, **Review Changes**, and **Discard Recovery**. Recovered text never overwrites canonical manuscript files merely because a journal exists; only an explicit restore action writes recovery content back to the project.

## Backups and restore

Timed and manual ZIP backups continue to live under `.typescribe/backups/`. **Project → Backup Manager…** presents backup timestamp, archive size, backup count, and an estimate of files changed since each backup. Restore always extracts into a unique new project directory (or a new directory under a user-selected parent) and then opens that restored copy. The active project directory is never overwritten in place. ZIP extraction canonicalizes every destination path and rejects entries that would escape the selected restore directory.

## Desktop workspace

The desktop shell is a resizable three-pane workspace:

```text
Binder / Search   |   Editor   |   PDF Preview / Outline
```

The outer shell owns desktop concerns such as menu commands, keyboard shortcuts, context menus, pane visibility, file pickers, drag/drop gestures, editor caret positioning, and PDF bitmap presentation. `WorkspaceViewModel` owns the corresponding application-facing state: selected binder item, outline focus, search results, preview scope, editor-navigation requests, and preview build lifecycle.

This separation keeps Avalonia-specific interaction mechanics out of the project/publishing services while preserving explicit AOT-friendly composition.

## Style model

`BookStyle` is presentation data, separate from manuscript content. `styles/book.style` persists page size, margins, body font and size, line spacing, paragraph indentation/spacing, and justification. The LaTeX generator consumes the same style model for live preview and final publishing.

## Search

The initial project search intentionally remains database-free. It scans manuscript files and supports title/content matching, case sensitivity, whole-word mode, and regular expressions with a timeout guard. A future indexed implementation can replace the service behind the same application workflow.

## LaTeX publishing pipeline

The only production typesetting pipeline is:

```text
UTF-8 manuscript / current editor buffer
      ↓
DocumentParser
      ↓
DocumentAst
      ↓
BookStyle
      ↓
DocumentRenderer.RenderLatex
      ↓
LuaLatexPublishingEngine
      ↓
PDF
```

The PDF adapter resolves LuaLaTeX in this order:

1. `TYPESCRIBE_LUALATEX`
2. Typescribe-managed full TinyTeX runtime
3. `lualatex` from `PATH`

If unavailable, Typescribe downloads the pinned full TinyTeX 2026.09 runtime for the current platform and verifies the archive SHA-256 before installation. The managed runtime lives in a versioned `tinytex-full-2026.09` directory so older minimal runtime installs are not reused accidentally.

LuaLaTeX always runs with `-no-shell-escape`, `-halt-on-error`, and file/line diagnostics enabled.

## Realtime PDF preview

The preview path uses the same parser, AST, style model, LaTeX generator, and LuaLaTeX executable as final publishing. The differences are source scope, scheduling, and pass count:

- Editor input is debounced by about 550 ms.
- Selecting a manuscript document makes that document the default preview source.
- Selecting an outline heading narrows the source to that heading's computed section and requests editor navigation to its source line.
- The user can explicitly switch preview scope to the whole included book.
- Preview source uses the current unsaved editor buffer for the selected document.
- A new edit cancels the previous debounce and, when needed, terminates the obsolete LuaLaTeX process tree.
- Preview uses one LuaLaTeX pass for lower latency.
- The result is atomically copied to `build/live-preview.pdf`.
- PDFtoImage/PDFium rasterizes the requested page and the Avalonia UI displays it in the inspector.
- Page navigation and zoom are handled entirely inside Typescribe.

The previous successfully rendered page remains visible while a newer preview is compiling, avoiding visual flicker during normal typing.

Final **Publish PDF** ignores temporary preview scope, flushes manuscript autosave, compiles the complete included book, and runs two LuaLaTeX passes for stable references and page numbering.

## Security boundaries

- Project-relative paths are canonicalized and checked against the project root.
- Binder file operations resolve through the project repository and destructive mutations use the transaction service.
- Drag/drop rejects self/descendant cycles before durable filesystem moves.
- Backup restore rejects ZIP entries that escape the destination directory.
- Crash recovery never overwrites canonical manuscript text without an explicit restore action.
- Manuscript prose is escaped before generated LaTeX output.
- LuaLaTeX receives generated source, not arbitrary manuscript shell commands.
- Shell escape is explicitly disabled.
- Downloaded TinyTeX archives are verified against pinned SHA-256 digests.
- Obsolete preview compiler processes are killed on cancellation.
- No manuscript telemetry/upload path exists in the v0.1 implementation.

## Known limitations

- Workspace dimensions, pane visibility, active tabs, and preview zoom are not yet persisted between sessions.
- The embedded PDF preview is page-raster based; exact cursor-to-PDF synchronization, text selection, thumbnail navigation, and virtualized multi-page scrolling are future work.
- Figures, tables, footnotes, citations, bibliography, and cross-references are not yet represented in the AST.
- Search is a file scan rather than an incremental SQLite FTS index.
- LuaLaTeX diagnostics are surfaced with focused compiler output but are not yet mapped back to AST/source locations in the editor.
