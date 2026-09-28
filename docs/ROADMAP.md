# Typescribe roadmap

## v0.1 authoring and publishing foundation — implemented

- Cross-platform Avalonia desktop shell
- Native AOT configuration and release scripts
- Local project creation/opening
- Plain UTF-8 manuscript storage
- Persistent hierarchical Binder with stable IDs
- Chapters, parts, folders, rename/delete/include-exclude
- Binder drag/drop reordering/reparenting with real filesystem moves
- Corkboard index-card planning view
- Corkboard card drag/reorder using Binder ordering
- Spreadsheet-style project Outliner
- Dynamic custom metadata columns
- Manual Collections keyed by stable binder IDs
- Saved project searches with matching options
- Document Inspector: synopsis, notes, status, label, keywords, word target
- Document comments/annotations with resolve/reopen/delete and line navigation
- Project, daily, session, and per-document writing targets
- Persisted daily target baseline and live session progress
- Immutable timestamped document snapshots
- Snapshot line-diff comparison
- Safe snapshot restore with automatic pre-restore snapshot
- Local project templates and New from Template workflow
- F11 distraction-reduced Composition Mode
- Markdown-like parser to canonical semantic AST
- Inline strong/emphasis/code/link/math
- Headings, quotes, ordered/unordered lists, code blocks, display math, thematic breaks
- Editor with debounced autosave and atomic writes
- Heading Outline with editor navigation
- Selection-scoped live preview for document or heading
- Optional whole-book live preview
- Unicode-aware word count
- Project title/content search with case/whole-word/regex options
- Persistent basic book style model/editor
- Style-aware LaTeX generation
- LuaLaTeX production compilation
- Verified full TinyTeX runtime setup when LuaLaTeX is unavailable
- Debounced realtime in-app PDF preview
- Cancellable one-pass preview compilation
- PDF page rendering with navigation and zoom
- Two-pass final PDF export
- LaTeX source export
- Whole-book compilation in Binder order
- Studio workspace: Binder/Search/Collections, Editor/Corkboard/Outliner, Inspector/Comments/PDF/Outline/Snapshots/Project
- Desktop menu bar, toolbar, context menus, and common keyboard shortcuts
- Cross-platform compile CI

## v0.1 follow-up hardening

- Persist window size, pane widths, active tabs, Corkboard/Outliner mode, and preview zoom
- Editable/sortable Outliner cells and configurable column visibility
- Freeform Corkboard layout, card stacks, and multi-selection drag
- Annotation anchors that automatically remap as source lines are inserted/deleted
- Collection folders and richer collection operators
- Template management UI: rename/delete/export/import templates
- Source-to-PDF cursor synchronization beyond heading/document scope
- Virtualized multi-page PDF scrolling and page thumbnails
- PDF text selection/search in preview
- Structured LuaLaTeX diagnostics mapped to manuscript source lines
- Undoable Binder filesystem operations
- Parser/render/persistence automated test suite
- Native AOT publish validation in CI
- Performance fixtures for very large projects
- Backup and crash recovery

## v0.2

- Footnotes/endnotes
- Figures and asset management
- Tables
- Cross-references
- Citation keys and bibliography model
- Incremental project index (SQLite FTS5)
- Richer collection queries over status, label, custom metadata, and comments
- Project statistics/dashboard and historical writing-session charts

## v0.3

- EPUB 3 exporter
- DOCX exporter
- Track changes
- Comment threads/replies
- Print profiles and preflight validation
- Richer template packs and project setup wizard

## v0.4+

- Plugin SDK with capability permissions
- Git integration
- Timeline/entity database
- Reference-manager adapters
- Advanced page designer
- CLI/headless build tool sharing the same Application/Domain layers
