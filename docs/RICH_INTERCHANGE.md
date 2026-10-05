# Rich multilingual interchange

TypeScribe keeps Markdown and logical Unicode text canonical. Clipboard, DOCX and EPUB are projections of that source; they never store visually reversed Arabic-script strings or substitute Arabic Presentation Forms into the manuscript.

## Clipboard

The manuscript editor writes these representations together, in fidelity order:

1. `typescribe.rich-markdown.v1` — exact TypeScribe Markdown, including rich metadata.
2. HTML — semantic structure plus `lang`, `dir`, CSS typography, OpenType features and variable axes.
3. RTF — Word/LibreOffice-oriented Unicode rich text with fonts, size, direction, language, bold/italic/underline, small caps and tracking.
4. Plain Unicode text — compatibility fallback.

Paste prefers the same order. TypeScribe-to-TypeScribe copy therefore remains exact while browser, Word and LibreOffice content uses formats those applications expose.

The HTML importer maps representable CSS into TypeScribe character/paragraph metadata. The RTF path maps the subset portable in RTF. Unsupported properties are not invented.

## DOCX

`RichDocxInterchangeService` builds on the established semantic DOCX exporter, then enriches generated WordprocessingML. Existing heading/list/table/figure/footnote/package behavior remains intact.

Paragraph mappings include `w:pStyle`, `w:bidi`, `w:jc`, `w:ind`, `w:spacing`, `w:keepNext`, `w:keepLines`, tabs and paragraph character defaults.

Character mappings include `w:rFonts`, `w:lang`, `w:rtl`, `w:sz`/`w:szCs`, bold, italic, underline, small caps, tracking (`w:spacing`), baseline shift (`w:position`), color and the closest portable Word kerning setting.

DOCX has no portable equivalent for arbitrary OpenType feature tags or variable-font axis coordinates. TypeScribe preserves Unicode text and supported properties, and reports a warning rather than silently claiming fidelity.

Import reconstructs supported `w:pPr` / `w:rPr` values as TypeScribe Markdown metadata.

## EPUB 3

The valid base EPUB package is enriched with block/run `lang`, `xml:lang`, `dir`, font family/size/weight/style/underline/small caps, tracking, kerning, ligatures, `font-feature-settings`, `font-variation-settings`, paragraph alignment/spacing/indents/line-height, break avoidance and hyphenation.

Physical tab-stop positions and optical margin alignment are not reliably portable in reflowable EPUB readers, so they produce warnings while canonical Markdown keeps the original metadata.

## Project-font embedding policy

TypeScribe does **not** silently copy arbitrary system fonts. A `FontSourceKind.Project` font is embedded in EPUB only when its project-relative path exists inside the project and is explicitly listed in `fonts/embedding-permissions.txt`.

Example:

```text
# fonts/embedding-permissions.txt
fonts/MyLicensedFont.otf
fonts/MyUrduFont.woff2
```

This is an explicit project-owner packaging opt-in, not a license detector. Users remain responsible for the font license. Non-approved fonts remain referenced by family name and produce an export warning.

## Warnings and validation

Desktop DOCX/EPUB commands write a sibling `<output>.warnings.txt` file when a property cannot be represented exactly. A stale warning file is removed when a later export has no warnings.

`tools/Typescribe.InterchangeSmoke` verifies private/HTML/RTF clipboard fidelity, DOCX WordprocessingML plus import, EPUB XHTML/CSS, refusal to embed an unapproved project font, and invariant logical Urdu/English Unicode order. Ubuntu CI runs EPUBCheck against both the established and rich multilingual EPUB smoke packages.
