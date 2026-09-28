# Typescribe roadmap

## v0.1 foundation — included here

- Cross-platform Avalonia desktop shell
- Native AOT configuration
- Local project creation/opening
- Plain-text manuscript storage
- Binder hierarchy from manuscript folders
- Chapter creation
- Markdown-like parser to canonical AST
- Editor + semantic live preview
- Debounced autosave with atomic writes
- Word count
- Project search
- Typst source export
- LaTeX source export
- Portable PDF publishing adapter
- Pinned/verified bundled publishing engine in release builds
- Whole-book compilation in binder order

## v0.2

- Persistent binder metadata and true drag/drop reordering
- Corkboard and outline views
- Rich metadata fields
- Footnotes/endnotes
- Figures and asset management
- Tables
- Cross-references
- Citation keys and bibliography model
- Incremental project index (SQLite FTS5)

## v0.3

- Project templates
- EPUB 3 exporter
- DOCX exporter
- Track changes and comments
- Immutable snapshots
- Print profiles and preflight validation

## v0.4+

- Plugin SDK with capability permissions
- Git integration
- Timeline/entity database
- Reference-manager adapters
- Advanced page designer
- Optional LuaLaTeX publishing bundle/profile
- CLI/headless build tool sharing the same Application/Domain layers
