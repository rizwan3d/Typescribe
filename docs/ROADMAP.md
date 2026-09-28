# Typescribe roadmap

## v0.1 foundation — implemented

- Cross-platform Avalonia desktop shell
- Native AOT configuration and release scripts
- Local project creation/opening
- Plain UTF-8 manuscript storage
- Persistent hierarchical binder
- Chapters, parts, and folders
- Binder rename/delete/reorder/include-exclude
- Drag/drop binder reordering and reparenting with real filesystem moves
- Markdown-like parser to canonical semantic AST
- Inline strong/emphasis/code/link/math
- Headings, quotes, ordered/unordered lists, code blocks, display math, thematic breaks
- Editor with debounced autosave and atomic writes
- Document heading outline with editor navigation
- Selection-scoped live preview for document or heading
- Optional whole-book live preview
- Word count
- Project title/content search
- Case-sensitive, whole-word, and regex search
- Persistent basic book style model/editor
- Style-aware LaTeX source generation
- LuaLaTeX production compilation
- Verified full TinyTeX runtime setup when LuaLaTeX is unavailable
- Debounced realtime in-app PDF preview from the current editor buffer
- Cancellable one-pass preview compilation
- PDF page rendering with navigation and zoom
- Two-pass final PDF export
- LaTeX source export
- Whole-book compilation in binder order
- Resizable Binder / Editor / Inspector workspace
- Binder/Search and PDF Preview/Outline tabs
- Desktop menu bar, toolbar, context menu, and common keyboard shortcuts
- Cross-platform compile CI

## v0.1 follow-up hardening

- Persist window, pane widths, active tabs, and preview zoom between sessions
- Source-to-PDF cursor synchronization beyond heading/document scope
- Virtualized multi-page PDF scrolling and page thumbnails
- PDF text selection/search in preview
- Structured LuaLaTeX diagnostics mapped to manuscript source lines
- Undoable binder filesystem operations
- Parser/render/persistence automated test suite
- Native AOT publish validation in CI
- Performance fixtures for very large projects
- Backup and crash recovery

## v0.2

- Corkboard and richer outline views
- Rich metadata fields and inspector
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
