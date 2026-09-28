# Third-party notices

Typescribe uses third-party components in published builds.

## Avalonia

- Project: Avalonia UI
- License: MIT
- Source: https://github.com/AvaloniaUI/Avalonia

## Typst

- Project: Typst
- Version pinned by the release scripts: 0.15.1
- License: Apache-2.0
- Source: https://github.com/typst/typst

The release scripts download an official Typst release asset for the target platform, verify its pinned SHA-256 digest before extraction, and embed the executable into the Typescribe build. Typst itself contains upstream embedded fonts; Typescribe does not redistribute separate font files.

Before redistributing a production build, review the current upstream license and notice files for every dependency and include any notices required by those licenses.
