# Paged layout architecture

TypeScribe's page view is a deterministic projection of the canonical Markdown document. It does not introduce a canvas file, hidden binary story, or second editable text stream.

## Source of truth

The layout engine consumes the same `DocumentAst`, `BookStyle.NamedStyles`, and `RichBlockFormatting` objects used by rich editing and export. Page-only semantics remain in ignorable `typescribe:block64` comments immediately before the Markdown block they decorate.

`RichBlockFormattingEditor` is the write path for the page inspector. It replaces or inserts one block-metadata comment by `AstBlock.SourceLine`; authored Markdown remains in logical source order. Clearing a page-layout property removes that component from the block metadata and removes the metadata line entirely when the block has no remaining rich properties.

## Deterministic pagination

`PagedLayoutEngine` uses point-based layout metrics derived from page styles, paragraph settings and semantic blocks. It deliberately does not persist layout coordinates. Given the same Markdown, metadata and style catalog, the engine returns the same:

- physical page and display-page sequence;
- section/page-style geometry and facing-page side;
- column bounds and block fragments;
- footnote reservation;
- table row continuation and repeated headers;
- linked text-frame thread/overset state;
- anchored/floating object placement;
- margin/column guides and baseline-grid projection;
- bleed, slug and crop-mark metadata.

The desktop view may render those boxes with platform text shaping, but page-flow decisions are recoverable from source and styles alone.

## Sections and parent pages

Named `PageStyleDefinition` records are the reusable parent/master-page layer. A section references its parent page with `SectionFormatting.PageStyleId`. Parent pages define trim size, margins, facing-page behavior, baseline grid, bleed, slug and crop marks.

Section metadata can override flow behavior without copying parent-page geometry:

- one or more columns plus column gap;
- continuous, next-page, next-odd or next-even starts;
- Arabic/Roman/alphabetic page-number style and optional restart;
- facing-page and baseline-grid behavior.

The Page Layout window edits named parent-page geometry through the normal project style store, so exporters and later sessions see the same geometry.

## Flow rules

Paragraph pagination honors direct formatting first, then named paragraph styles. `KeepWithNext`, `KeepLinesTogether`, `KeepFirstLines` and `KeepLastLines` are applied before a split. When explicit first/last-line counts are absent and `BookStyle.AvoidWidowsAndOrphans` is enabled, the engine uses two-line widow/orphan minima.

A keep-together object larger than one column is split only when there is no lossless alternative and produces a layout warning where appropriate.

## Tables, notes and floats

Tables are paginated by semantic rows. Header rows come from the Markdown header plus configured repeat-header rows and are repeated when a table continues into another column/page. Row minimum heights and keep information participate in measurement.

Footnote references reserve space at the bottom of the page where the reference is laid out. Definitions stay ordinary Markdown footnote definitions and are never duplicated into canonical source.

Figures use their semantic figure size/style plus optional `AnchoredObjectFormatting`. Inline/here/top/bottom/page placements participate in flow. Margin placement becomes a side object; wrap mode and wrap insets remain attached to the anchor metadata for richer renderers/exporters.

## Linked text frames and overset

`TextFrameFormatting.Id` identifies a story frame and `NextFrameId` links it to the next frame. The engine pre-scans the AST, validates duplicate/missing/cyclic links, and flows fragments through the resulting chain. If content remains after the final frame, layout emits an `OversetIndicator` and an `overset` warning instead of dropping or truncating source text.

## Page Layout window

Use **View → Page Layout…** (`Ctrl/Cmd+Shift+P`) for the live page projection. The window provides:

- page/spread cards with physical rulers;
- margin and column guides;
- baseline-grid visualization;
- selectable source-backed block/frame/object boxes;
- parent-page geometry editing;
- section columns/start/numbering/facing-page/baseline controls;
- text-frame ID/thread controls;
- anchor placement and wrap controls;
- overset and layout diagnostics;
- **Go to source** navigation back to the manuscript editor.

Clicking a box selects the corresponding `AstBlock.SourceLine`. Applying an inspector change rewrites canonical Markdown metadata or the named style catalog and immediately re-paginates.

## Regression coverage

`tools/Typescribe.LayoutSmoke` runs in CI and checks:

- repeated layout calls produce an identical page/fragment fingerprint;
- long source flows over multiple pages without source mutation or reordering;
- multi-column and baseline-grid sections;
- odd/even section starts and Roman/Arabic page-number restarts;
- footnote reservation;
- margin anchors and wrapping metadata;
- table continuation with repeated headers;
- linked frames and deliberate overset diagnostics;
- block-metadata insertion/removal round-trips through `RichDocumentParser`.

The test intentionally reconstructs page layout from semantic input rather than comparing a persisted page cache, because recoverability from Markdown is part of the contract.
