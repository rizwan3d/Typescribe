# Unicode BiDi editor architecture

TypeScribe keeps logical Unicode text and Markdown as the canonical manuscript. It never stores Arabic presentation forms, reverses strings, or rewrites mixed-direction text to match visual order.

## Why RTL paragraphs use inline native editors

AvaloniaEdit 12.0 creates an internal `VisualLineTextParagraphProperties` with `FlowDirection.LeftToRight` and `TextAlignment.Left`. Those paragraph properties are not currently replaceable from TypeScribe without maintaining a fork.

For right-to-left manuscript lines, TypeScribe instead uses `InlineBidiParagraphEditorFeature` to replace the source range in AvaloniaEdit's visual line with an `InlineObjectElement` containing a native Avalonia `TextBox`.

This is not a detached overlay. The control occupies the source range in the editor's text flow, and edits are committed back to exactly that Markdown line.

The native Avalonia text stack is responsible for:

- Unicode BiDi mixed-run layout
- visual caret and selection behavior
- word/Home/End navigation
- Arabic-script OpenType shaping through the platform text shaper
- logical-order clipboard text

## Base-direction resolution

Direction is resolved in this order:

1. `ParagraphFormatting.Direction = RightToLeft` forces RTL.
2. `ParagraphFormatting.Direction = LeftToRight` forces LTR.
3. `Auto` uses Unicode first-strong detection.

Neutral Markdown punctuation, digits and emoji do not decide the paragraph direction. As a result, a line such as `**اردو 2026**` resolves to RTL without any TypeScribe metadata.

## Script and font hints

`UnicodeScriptClassifier` is rune-aware and detects Arabic-script, Hebrew, Syriac, Thaana and N'Ko RTL text. It also distinguishes Urdu and Persian hints where possible.

When an RTL paragraph has no explicit TypeScribe font, the inline editor searches installed system fonts for a suitable family. Urdu prefers Nastaliq-capable families (for example Noto Nastaliq Urdu or Awami Nastaliq) when installed. An explicit paragraph/character font always wins.

Font fallback changes only font selection. The Unicode source string is never altered.

## Tables

Inline Markdown table cells are already native Avalonia `TextBox` controls. The BiDi feature detects those controls under the manuscript editor and applies RTL/right alignment to Arabic-script cells automatically.

## Markdown contract

A formatted RTL paragraph remains ordinary text preceded by optional ignorable TypeScribe metadata:

```markdown
<!-- typescribe:block64:... -->
یہ ایک اردو پیراگراف ہے۔
```

The metadata can be removed and the text still opens correctly in any Unicode-aware Markdown editor.

## Regression fixtures

`tools/Typescribe.BidiSmoke` verifies:

- Arabic, Urdu and Persian first-strong direction
- mixed English/Arabic base direction
- Markdown-neutral prefixes
- neutral-only content
- Urdu/Persian/Arabic/Hebrew script hints
- classification never mutates logical source strings

The smoke suite runs on Ubuntu CI.

## Page-layout follow-up

A mirrored physical ruler and page-frame tab/indent controls belong to the paged-layout subsystem tracked in #43. The paragraph editor already preserves tab characters and RTL base direction so that subsystem can consume the same Markdown-first paragraph metadata.
