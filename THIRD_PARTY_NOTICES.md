# Third-party notices

Typescribe uses third-party components and can download optional publishing components at runtime.

## Avalonia

- Project: Avalonia UI
- License: MIT
- Source: https://github.com/AvaloniaUI/Avalonia

## AvaloniaEdit

- Project: AvaloniaEdit
- Version: 12.0.0
- License: MIT
- Source: https://github.com/AvaloniaUI/AvaloniaEdit

AvaloniaEdit provides the cross-platform, virtualized text engine underneath Typescribe's `ManuscriptEditor`. Typescribe supplies its own manuscript rendering and authoring semantics on top of that engine.

## LibreOffice English Hunspell dictionaries / SCOWL

Typescribe can download English Hunspell dictionaries (`en_US`, `en_GB`, `en_CA`, and `en_AU`) from the public LibreOffice dictionaries repository and cache them under the user's local application-data directory for offline spell checking.

- LibreOffice dictionaries source: https://github.com/LibreOffice/dictionaries
- Pinned source revision: `32b006a2c22a4ac7e8ed3f03346f7b3d85a970a4`
- English dictionary documentation and licensing: https://github.com/LibreOffice/dictionaries/blob/32b006a2c22a4ac7e8ed3f03346f7b3d85a970a4/en/README_en_US.txt
- Upstream word-list project: SCOWL / Kevin Atkinson's word lists

The English dictionaries are derived from SCOWL and other sources described in the upstream README. Their copyright, permission notices, public-domain statements, and other upstream license terms continue to apply. Typescribe does not modify the downloaded dictionary files.

## PDFtoImage

- Project: PDFtoImage
- Version: 5.4.0
- License: MIT
- Source: https://github.com/sungaila/PDFtoImage

PDFtoImage provides the cross-platform PDF-page rendering bridge used by the in-app live preview. Its runtime dependencies include PDFium and SkiaSharp; their upstream licenses and notices continue to apply to redistributed builds.

## SkiaSharp

- Project: SkiaSharp
- License: MIT
- Source: https://github.com/mono/SkiaSharp

## PDFium

PDF page rasterization ultimately uses PDFium through the PDFtoImage dependency chain. PDFium is an upstream Chromium PDF component and carries its own third-party notices and licenses. Review the native PDFium package notices included by the dependency before redistributing production binaries.

## TinyTeX / TeX Live

Typescribe can download a version-pinned **TinyTeX 2026.09** runtime when LuaLaTeX is not otherwise available.

- TinyTeX release project: https://github.com/rstudio/tinytex-releases
- TeX Live project: https://tug.org/texlive/

TinyTeX is a distribution mechanism for TeX Live. TeX Live contains many independently licensed packages; the applicable package licenses and notices remain those supplied by the corresponding upstream projects and TeX Live distribution. Typescribe verifies the pinned runtime archive digest before installing it under the user's local application-data directory.

The Typescribe source repository does not contain the TinyTeX/TeX Live runtime itself.

Before redistributing a production build together with additional third-party runtimes, review the current upstream licenses and notices and include anything required by those components.
