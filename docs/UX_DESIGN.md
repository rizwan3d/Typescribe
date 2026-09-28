# Typescribe UI/UX design principles

Typescribe uses a platform-neutral desktop visual language. The goal is to feel polished on Windows, macOS, and Linux without imitating any one operating system.

## Principles

- Keep the native operating-system window frame and title controls.
- Follow the system light/dark preference by default.
- Use Typescribe's own neutral slate surfaces and indigo accent rather than platform-specific chrome.
- Preserve information density for long-form writing, but keep primary actions visually distinct.
- Prefer a clear three-zone hierarchy: navigation, writing/planning, inspection/publishing.
- Use restrained 8–12px internal corner radii, subtle borders, and minimal decorative effects.
- Keep typography calm and readable; manuscript content should dominate the workspace.
- Keep hover, selected, focus, and disabled states obvious enough for keyboard and mouse users.
- Avoid fake macOS traffic-light controls, Windows ribbon imitation, or Linux-desktop-specific styling.
- Reuse semantic visual tokens instead of hard-coded colors in feature code.

## Semantic color roles

`StudioTheme.axaml` defines theme-aware resources for:

- application background
- primary surface
- muted surface
- hover surface
- editor surface
- border
- primary and muted text
- Typescribe accent
- soft accent selection
- danger state

Light and dark values live in Avalonia theme dictionaries and are referenced with dynamic resources, so controls respond when the effective theme changes.

## Interaction hierarchy

Primary actions such as **Publish PDF** use the accent treatment. Common navigation and editing actions use neutral surfaces. Compact controls are reserved for small utility actions such as page navigation or zoom.

The Binder, Inspector, Corkboard cards, search results, comments, collections, and Outliner should all use the same selection and surface system. New features should avoid introducing one-off colors or platform-specific icons unless they carry real semantic meaning.

## Writing canvas

The manuscript editor is intentionally quieter than surrounding tools:

- stronger internal padding
- dedicated editor surface
- minimal chrome
- clear focus border
- readable source font sizing

Composition Mode remains the most distraction-reduced experience and should not add decorative UI.

## Platform behavior

Typescribe should behave consistently across operating systems while respecting native conventions for window management, file pickers, keyboard modifiers, text rendering, and accessibility. Platform-neutral does not mean custom-drawing everything; where the operating system already provides a familiar native behavior, Typescribe should keep it.
