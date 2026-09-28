# Typescribe roadmap

## v0.1 foundation — implemented

- Cross-platform Avalonia desktop shell
- Native AOT configuration and release scripts
- Local project creation/opening
- Plain UTF-8 manuscript storage
- Persistent hierarchical binder
- Chapters, parts, and folders
- Binder rename/delete/reorder/include-exclude
- Markdown-like parser to canonical semantic AST
- Inline strong/emphasis/code/link/math
- Headings, quotes, ordered/unordered lists, code blocks, display math, thematic breaks
- Editor + semantic live writing preview
- Debounced autosave with atomic writes
- Word count
- Project title/content search
- Case-sensitive, whole-word, and regex search
- Persistent basic book style model/editor
- Style-aware LaTeX and Typst source generation
- LuaLaTeX production compilation
- Verified portable TinyTeX runtime setup when LuaLaTeX is unavailable
- Production PDF preview (`build/preview.pdf` opened in the OS viewer)
- PDF export
- LaTeX source export
- Typst source export
- Whole-book compilation in binder order
- Cross-platform compile CI

## v0.1 follow-up hardening

- Drag/drop binder reparenting
- Embedded paginated PDF preview canvas
- Source-to-PDF synchronization
- Structured LuaLaTeX diagnostics mapped to manuscript source lines
- Parser/render/persistence automated test suite
- Performance fixtures for very large projects
- Backup and crash recovery

## v0.2

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
- CLI/headless build tool sharing the same Application/Domain layers
