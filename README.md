# Typescribe

Typescribe is a local-first, cross-platform C# desktop foundation for long-form writing and professional typesetting. It is designed around a canonical semantic AST, plain UTF-8 manuscript files, renderer independence, and portable Native AOT releases.

## Technology choices

- **.NET 10 / C# 14**
- **Avalonia 12.1.3** for one desktop UI codebase on Windows, macOS, and Linux
- **Native AOT** for self-contained platform-native releases; end users do not install the .NET runtime
- **Typst 0.15.1** as the bundled zero-setup PDF engine for the initial portable release
- **LaTeX source export** behind the same renderer abstraction, leaving room for a future bundled LuaLaTeX publishing profile
- **Plain UTF-8 + YAML-shaped manifest** for project ownership and portability

WPF is intentionally not used because it is Windows-only. The UI is code-only Avalonia, which also avoids runtime XAML loading and keeps the Native AOT surface straightforward.

## Included v0.1 foundation

- Create/open a local Typescribe project
- Hierarchical manuscript binder derived from folders
- Create chapters
- Markdown-like source editor
- Canonical document AST
- Semantic live preview
- Debounced autosave (~850 ms) with atomic file replacement
- Unicode-aware word counting
- Full-project text search
- Whole-book compilation in binder order
- Typst source export
- LaTeX source export
- PDF publishing through a version-pinned bundled Typst engine
- SHA-256 verification of the publishing engine during packaging and again after runtime extraction
- AOT-friendly explicit composition root; no reflection-based DI container
- Portable release scripts and GitHub Actions matrix for Windows x64, Linux x64, and macOS Apple Silicon

## Architecture

```text
Typescribe.Desktop           Avalonia UI + composition root
        │
        ├───────────────┐
        ▼               ▼
Typescribe.Application  Typescribe.Infrastructure
        │               filesystem, atomic saves,
        ▼               publishing adapters
Typescribe.Domain
project tree + canonical AST
```

Dependencies point inward. `Domain` is framework-free. `Application` defines ports/use cases. `Infrastructure` implements persistence and publishing adapters. `Desktop` composes concrete implementations.

Patterns used include Repository, Strategy/Adapter, Ports & Adapters, Presentation Model, Composition Root, debounce, and atomic replace. See `docs/ARCHITECTURE.md` for the rationale.

## Project format

```text
my-book/
  typescribe.yaml
  manuscript/
    chapter-01.md
  research/
  assets/
  styles/
  build/
```

The manuscript remains readable/editable outside Typescribe. Generated `.typ`, `.tex`, and PDF files are outputs, not the canonical manuscript.

## Development

Prerequisite for developers: .NET 10 SDK.

```bash
dotnet restore Typescribe.slnx
dotnet run --project src/Typescribe.Desktop/Typescribe.Desktop.csproj
```

A normal development run does not require Typst, but PDF publishing is disabled unless the engine resource is present. Source/preview/editing still work.

## Build a zero-install portable release

The packaging scripts download the pinned official Typst release, verify its SHA-256 digest, embed the executable into Typescribe, and then Native-AOT-publish the desktop app. This setup is for the **builder** only; the resulting archive is portable for the end user.

### Windows x64

```powershell
./scripts/publish.ps1 -Rid win-x64
```

Output: `artifacts/Typescribe-win-x64.zip`

### Linux x64

```bash
./scripts/publish.sh linux-x64
```

Output: `artifacts/Typescribe-linux-x64.tar.gz`

### macOS Apple Silicon

```bash
./scripts/publish.sh osx-arm64
```

Output: `artifacts/Typescribe-osx-arm64.zip` containing `Typescribe.app`.

macOS Intel is also supported by `./scripts/publish.sh osx-x64`.

Native AOT output is OS/architecture-specific, so there is one portable bundle per target platform rather than one executable that runs unchanged on all operating systems.

## Release automation

`.github/workflows/release.yml` builds portable artifacts on native GitHub-hosted runners when manually dispatched or when a `v*` tag is pushed. This is also the easiest way to produce all platform bundles without setting up all three operating systems locally.

## Current limitations

This is the requested architectural/MVP source foundation, not the entire 100+ feature product specification. The current preview is semantic text rather than a paginated page canvas; drag/drop binder editing, citations, figures, footnotes, SQLite FTS, EPUB/DOCX, comments/track changes, plugin sandboxing, and advanced page design are roadmap work. LaTeX-style display math is preserved in `.tex` export but is emitted as a literal code block in the initial Typst PDF path until a proper math AST/renderer is added.

## Security and privacy defaults

Typescribe performs manuscript work locally. Project-relative paths are canonicalized before file access, persistence uses atomic replacement, no manuscript telemetry/upload path is present, and PDF publishing receives generated renderer output rather than manuscript-provided shell commands.

## Repository notes

No source-code license has been selected for Typescribe in this bundle. `THIRD_PARTY_NOTICES.md` documents bundled third-party components. Choose an application license before public distribution.
