# Markdown-first rich document model

TypeScribe should behave like a word processor/page-layout application without making the manuscript an opaque binary document. The canonical authored source remains Markdown; rich semantics are stored in small, ignorable TypeScribe metadata comments and reusable project styles.

## Design rules

1. **Markdown is canonical.** Text, headings, lists, links, citations, footnotes, tables and figures remain normal Markdown wherever Markdown can represent them.
2. **Metadata is additive.** TypeScribe-only formatting and layout use `<!-- typescribe:* -->` comments. Removing those comments leaves a readable document.
3. **Styles beat direct formatting.** Named paragraph/character/table/cell/page styles are the preferred representation. Direct formatting is reserved for local overrides.
4. **The editor renders semantics, not a second document.** Rich tables and future inline objects are visual elements anchored to source ranges in AvaloniaEdit. Their edits immediately rewrite canonical Markdown.
5. **Exporters consume the same semantics.** LuaLaTeX, EPUB and DOCX should map from the same Markdown + metadata + style catalog rather than each maintaining private formatting state.

## Persistence format

### Block formatting

A block attribute is attached with a Base64-encoded UTF-8 JSON comment immediately before the Markdown block:

```markdown
<!-- typescribe:block64:BASE64_JSON -->
یہ ایک اردو پیراگراف ہے۔
```

The decoded payload is `RichBlockFormatting`. It can carry paragraph style/typography, language/script/direction, keep options, tabs, paragraph rules, drop caps, section columns, page-number behavior, baseline grid, text-frame threading and anchored-object properties.

### Character/selection formatting

A selection remains Markdown and is wrapped by paired ignorable comments:

```markdown
<!-- typescribe:inline64:BASE64_JSON -->منتخب متن<!-- /typescribe:inline -->
```

The decoded payload is `CharacterFormatting`. It supports font references, size, weight/style, underline, small caps, ligatures, kerning, tracking, baseline shift, color, language/script/direction, OpenType features and variable-font axes.

### Fonts

`FontReference` distinguishes:

- `System`: use an installed system font by family name.
- `Project`: use a project-relative font asset so the project remains portable.
- `Embedded`: an exporter/package may embed the referenced font when licensing permits.

The project-relative path, not an absolute workstation path, should be persisted for custom fonts.

## Inline tables

Pipe tables remain canonical Markdown. `InlineMarkdownTableEditorFeature` uses AvaloniaEdit `InlineObjectElement` instances to replace individual table source lines in the visual flow. It does **not** collapse source lines and does **not** create a detached table editor below the manuscript.

All mutations go through `TableEditingEngine` and `TableMarkupCodec`, so row/column changes, alignment, captions, identifiers and merge metadata continue to round-trip in Markdown.

## RTL, Arabic, Urdu and Persian

The data model is Unicode-first. `TextDirectionMode`, `ScriptMode`, language tags and font references are kept independently so the same paragraph can preserve semantics through editor, DOCX, EPUB and LuaLaTeX.

Arabic-script shaping and Unicode BiDi are separate concerns:

- Avalonia/Skia can shape complex scripts when the selected font supports them.
- AvaloniaEdit 12.0 currently fixes visual-line paragraph direction to left-to-right in `VisualLineTextParagraphProperties`. A correct RTL editor therefore needs a patched/upstream AvaloniaEdit paragraph-direction hook or a replacement text-flow layer; setting only the outer control's `FlowDirection` is not sufficient.
- Urdu Nastaliq requires an appropriate Nastaliq OpenType font plus shaping. TypeScribe should not fake Nastaliq by transforming Unicode text.
- Paragraph direction should default from Unicode strong characters and/or the language tag, but users must be able to explicitly choose Auto/LTR/RTL.

## Feature map

| Capability | Markdown representation | TypeScribe semantic representation | Editor/export direction |
| --- | --- | --- | --- |
| Per-block font/style | Markdown text + block comment | `ParagraphFormatting`, named paragraph styles | Rich renderer + all exporters |
| Selection font/character formatting | Markdown text + paired inline comments | `CharacterFormatting`, character styles | Rich renderer + all exporters |
| Arabic/Urdu/Persian language | Unicode text | language + script + direction | BiDi/shaping-aware editor/export |
| Inline tables | Pipe Markdown + table metadata | `TableBlock`/`TableEditingEngine` | Inline object editor implemented |
| Multi-column sections | block metadata | `SectionFormatting.Columns` | layout engine/export |
| Text frames | block metadata | `TextFrameFormatting` | page-layout view/export |
| Linked/threaded text frames | block metadata | `TextFrameFormatting.NextFrameId` | page-layout view/export |
| Master/parent pages | project/page styles | named page styles + page templates | page-layout view/export |
| Page guides | project layout metadata | page/workspace model | editor-only layout aid |
| Ruler | no source syntax | editor UI | paragraph indents/tabs |
| Tabs/tab stops | block metadata | `TabStop` | editor/export |
| Baseline grids | section/page metadata | `BaselineGridFormatting` | page-layout view/export |
| Anchored objects | Markdown figure/object + metadata | `AnchoredObjectFormatting` | editor/export |
| Text wrap around images | object metadata | `TextWrapMode` + wrap insets | page-layout/export |
| Drop caps | block metadata | `DropCapFormatting` | editor/export |
| Paragraph rules | block metadata | `ParagraphRule` | editor/export |
| Optical margin alignment | block/style metadata | `OpticalMarginAlignment` | typesetter/export |
| Kerning | inline/style metadata | `CharacterFormatting.Kerning` | shaping/export |
| Tracking | inline/style metadata | `TrackingEm` | shaping/export |
| OpenType features | inline/style metadata | tag/value settings | shaping/export |
| Small caps | inline/style metadata | `SmallCaps` | shaping/export |
| Ligatures | inline/style metadata | `Ligatures` | shaping/export |
| Stylistic sets | inline/style metadata | OpenType `ss01`…`ss20` | shaping/export |
| Variable-font axes | inline/style metadata | axis tag/value settings | shaping/export |
| Footnote layout | Markdown footnotes + style metadata | existing footnote AST + styles | layout/export |
| Side notes | Markdown reference + metadata | anchored/margin placement | layout/export |
| Floating figures | Markdown figure + metadata | placement + anchor | layout/export |
| Table continuation across pages | table metadata/style | repeat header + row-break/keep controls | paged layout/export |
| Keep options | block/style metadata | keep-with-next/keep-lines/first/last | paged layout/export |
| Section starts | block metadata | `SectionStartMode` | paged layout/export |
| Page-number styles | section metadata | `PageNumberStyle` + start | paged layout/export |
| Spreads | page style | `FacingPages` | page-layout view/export |
| Bleed | page/section metadata | bleed values | print PDF/export |
| Slug | page/section metadata | slug value | print PDF/export |
| Crop marks | page/section metadata | crop-mark flag | print PDF/export |
| Preflight | derived project analysis | diagnostics, assets, fonts, links, overflow | preflight panel |
| Package for print | project operation | manuscript/assets/fonts/profile | packaged folder/zip |

## Implementation sequence

### 1. Editing foundation

- True inline table editing anchored to Markdown ranges.
- Rich metadata codec.
- Advanced style properties.
- Typography/paragraph inspector that writes the same metadata.
- Hide metadata markers from the visual editor while preserving source.

### 2. Multilingual text engine

- Add a paragraph-direction hook to the editor engine.
- Unicode BiDi for mixed-direction paragraphs.
- HarfBuzz/Skia shaping with language/script-aware fallback.
- Urdu Nastaliq presets that select a capable user/system/project font rather than modifying text.
- RTL paragraph controls, mirrored ruler/tab UI and RTL table-cell editing.

### 3. Interchange

- Rich clipboard reader/writer for `text/plain`, `text/html`, RTF where available, and TypeScribe's own rich Markdown flavor.
- DOCX import/export mapping for language, RTL, fonts, styles, tabs, keeps and OpenType-compatible properties.
- EPUB CSS generation for direction, language, embedded/project fonts and typography while preserving Unicode text.

### 4. Page-layout model

- Sections, columns, text frames/threading, anchors/wrap and baseline grids.
- Text frames default to automatic document flow, with optional source-backed X/Y/width/height/page-offset geometry when the author directly manipulates a frame on the spread canvas.
- Frame insets and internal columns are semantic layout properties; linked frames continue to use `NextFrameId`, and overset remains a diagnostic rather than hidden/truncated content.
- Parent pages, guides, ruler and spreads.
- Footnotes/sidenotes/floats/table continuation and keep rules.

### 5. Print production

- Bleed/slug/crop marks in LuaLaTeX/PDF output.
- Preflight for missing fonts/assets, unsupported glyphs, overflow, low-resolution images and broken references.
- Package-for-print that gathers the manuscript, assets, permitted fonts and publishing profile with a manifest.

## Compatibility policy

Unknown TypeScribe metadata must be preserved when possible and ignored safely when not understood. Exporters should warn rather than silently discard unsupported rich properties. The raw manuscript should remain usable in Git, ordinary Markdown editors and diff/merge tools.
