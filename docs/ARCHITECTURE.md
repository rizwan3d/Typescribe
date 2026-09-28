# Typescribe architecture

## Goals

Typescribe is a local-first, cross-platform authoring and typesetting desktop application. The architecture intentionally keeps manuscript content independent from presentation and rendering technology.

## Layering

```text
Typescribe.Desktop
    │  composition root + Avalonia UI
    ▼
Typescribe.Application
    │  use-case contracts, parser/render abstractions, search/word-count services
    ▼
Typescribe.Domain
    │  project tree + canonical document AST
    ▼
Typescribe.Infrastructure
       filesystem repository, atomic persistence, publishing adapters
```

Dependencies point inward. Domain has no package dependencies. Application depends only on Domain. Infrastructure implements Application ports. Desktop is the composition root and is the only project that knows concrete implementations.

## Design patterns

- **Repository** — `IProjectRepository` hides filesystem persistence.
- **Strategy / Adapter** — `IPdfPublishingEngine` isolates the concrete publishing backend.
- **Ports and Adapters** — Application interfaces define boundaries; Infrastructure provides adapters.
- **Presentation Model** — `WorkspaceViewModel` holds UI state without file-picker or Avalonia dependencies.
- **Composition Root** — `App` creates concrete services explicitly, avoiding reflection-heavy runtime DI.
- **Debounce** — editor changes are autosaved after a short idle period.
- **Atomic Replace** — saves are written to a sibling temporary file, flushed, then atomically replaced where supported.

## AOT decisions

- `net10.0` + Native AOT.
- No runtime assembly discovery, dynamic proxy generation, or expression compilation.
- No JSON reflection serializer in the project format.
- Code-only Avalonia UI removes runtime XAML loading concerns.
- Libraries declare `IsAotCompatible=true`.
- The publishing engine is an embedded, version-pinned resource extracted only when PDF publishing is requested.

## Canonical document model

Markdown-like source is parsed into `DocumentAst`. Preview, Typst, and LaTeX are projections from this AST. Renderer formats are never the canonical manuscript source.

Current AST blocks:

- Heading
- Paragraph
- Quote
- List item
- Code block
- Display math

The AST is intentionally small in v0.1 so citations, figures, footnotes, semantic blocks, tables, and cross-references can be added without coupling the editor to a renderer.

## Project format

```text
my-book/
  typescribe.yaml
  manuscript/
    chapter-01.md
  research/
  assets/
  styles/
  build/
```

Manuscript files are UTF-8 plain text and remain usable without Typescribe. Binder hierarchy is currently derived from folders under `manuscript/`.

## Publishing

The zero-setup release path uses a bundled Typst executable for PDF output. Typescribe also generates LaTeX source through the renderer abstraction. A future `LuaLatexPublishingEngine` can be added without changing the editor, parser, AST, or project storage.

The reason Typst is the default portable engine in this foundation is deployment size and simplicity: a full LuaLaTeX distribution is much larger. The interface deliberately avoids making this decision permanent.

## Security boundaries

- Project-relative paths are canonicalized and checked against the project root.
- User manuscript text is escaped before being inserted into generated Typst/LaTeX prose.
- PDF compilation receives generated source, not arbitrary shell commands.
- The bundled Typst archive is checksum-verified at build time.
- The embedded Typst executable is SHA-256 verified again after runtime extraction.
- No manuscript telemetry or uploads exist in the foundation.

## Known v0.1 limitations

- Binder rows show hierarchy but do not yet support drag/drop.
- The preview is semantic text, not a paginated canvas.
- Inline Markdown emphasis is not yet represented in the AST.
- LaTeX-style math is preserved for `.tex` output; richer math translation for Typst PDF is future work.
- Whole-book compilation currently concatenates included manuscript documents in binder order; section-level compile profiles are future work.
- No database is used yet; search is a straightforward file scan suitable for the initial MVP.
