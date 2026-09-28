# Typescribe architecture

## Goals

Typescribe is a local-first, cross-platform authoring and typesetting desktop application. Manuscript content stays independent from presentation and from the concrete publishing engine.

## Layering

```text
Typescribe.Desktop
    │  Avalonia UI + composition root
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

Domain has no package dependencies. Application depends only on Domain. Infrastructure implements Application ports. Desktop composes concrete implementations.

## Design patterns

- **Repository** — `IProjectRepository` hides project filesystem and binder metadata persistence.
- **Strategy / Adapter** — `IPdfPublishingEngine` isolates the production compiler.
- **Ports and Adapters** — Application interfaces define the boundaries implemented by Infrastructure.
- **Presentation Model** — `WorkspaceViewModel` owns desktop state without file-picker dependencies.
- **Composition Root** — `App` explicitly creates services and avoids reflection-heavy DI.
- **Debounce** — manuscript changes autosave after a short idle period.
- **Atomic Replace** — writes use sibling temporary files before replacement.

## AOT decisions

- `net10.0` + Native AOT release configuration.
- No dynamic proxy container or runtime assembly scanning.
- No reflection-based project serializer.
- Code-only Avalonia UI.
- Libraries declare AOT compatibility where applicable.
- LuaLaTeX executes as an isolated external publishing process rather than being loaded into the application process.

## Canonical document model

Markdown-like manuscript source is parsed into `DocumentAst`. Renderer formats are projections from this model and are never canonical manuscript content.

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

The parser can be extended with figures, tables, citations, footnotes, semantic blocks, and cross-references without tying the editor to LaTeX syntax.

## Binder model

The physical manuscript hierarchy remains under `manuscript/`. Binder-specific information that does not belong in manuscript text is stored in `.typescribe/binder.tsv`.

The sidecar currently persists:

- Node type
- Include/exclude state
- Project-relative path
- Display title
- Sibling ordering through record order

Filesystem paths remain the durable ownership boundary, while the sidecar provides application-specific organization without introducing a proprietary manuscript database.

## Style model

`BookStyle` is presentation data, separate from manuscript content. `styles/book.style` persists the initial style surface:

- Page size
- Margins
- Body font and size
- Line spacing
- Paragraph indentation/spacing
- Justification

Both the LaTeX and Typst generators consume the same style model.

## Search

The initial project search intentionally remains database-free. It scans manuscript files and supports title/content matching, case sensitivity, whole-word mode, and regular expressions with a timeout guard. A future indexed implementation can replace the service behind the same application workflow.

## Publishing

The production pipeline is:

```text
UTF-8 manuscript
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
2. Typescribe-managed TinyTeX runtime
3. `lualatex` from `PATH`

If unavailable, the user can ask Typescribe to download a pinned TinyTeX 2026.09 runtime. The archive SHA-256 is verified before installation. LuaLaTeX is invoked twice for stable references and with `-no-shell-escape`.

**Preview PDF** and **Publish Book PDF** use the same production pipeline. Preview writes `build/preview.pdf` and opens it with the operating system PDF viewer. An embedded paginated PDF canvas is later UI work; the compiled preview itself is already the production renderer's output.

Typst remains an optional generated source format only.

## Security boundaries

- Project-relative paths are canonicalized and checked against the project root.
- Binder file operations resolve through the project repository.
- Manuscript prose is escaped before generated LaTeX/Typst output.
- LuaLaTeX receives generated source, not arbitrary manuscript shell commands.
- Shell escape is explicitly disabled.
- Downloaded TinyTeX archives are verified against pinned SHA-256 digests.
- No manuscript telemetry/upload path exists in the v0.1 implementation.

## Known limitations

- Binder movement is currently explicit up/down plus selected-container insertion, not drag/drop reparenting.
- The writing preview is semantic text; production PDF preview opens the compiled PDF in the OS viewer rather than an embedded page canvas.
- Figures, tables, footnotes, citations, bibliography, and cross-references are not yet represented in the AST.
- Search is a file scan rather than an incremental SQLite FTS index.
- Compile diagnostics are surfaced as LuaLaTeX output but are not yet mapped back to AST/source locations.
