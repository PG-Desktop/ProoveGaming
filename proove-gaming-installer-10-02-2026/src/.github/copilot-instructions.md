# Copilot Instructions

## User Preferences
- Include an installer project folder structure that always contains a `Views` folder for UI files; document this convention in Copilot instructions.
- Make classes like `MainWindow` and related installer view classes `internal` by default; use internals visibility for tests when needed.
- Load localized installer content from separate language-specific files (e.g., en/uk) instead of performing runtime string translation or transformation in code. For uninstall flow, resolve language from a persisted registry preference in non-debug runs; allow language override in debug runs for testing and fall back to a default language if the registry preference is missing.
- Store license text files outside `Templates` (e.g., in `Content` or `Assets`); `Assets` is acceptable.
- If base UI components are reused more than once across the solution, implement them as shared components instead of duplicating per view/project.
- Ensure the app, installer, and uninstaller executables run directly from the EXE without requiring users to install any additional framework/runtime (self-contained deployment expectation).
- Desktop and Start Menu shortcut creation in the installer is optional via checkboxes, enabled by default.
- Temporary release/testing distribution is done via manual GitHub Actions workflow that publishes installer EXE and uploads a ZIP artifact.
- The installer welcome area should be smaller to ensure the full SVG border and buttons are visible.
- Language selectors in the installer should include icons for English and Ukrainian.
- Avoid magic strings for localization and language persistence; define reusable constants in shared/common code and use them from both App and Installer.

### Installer UI
- Extract shared styles into a centralized/shared style location when two or more UI components share the same style behavior; avoid per-view duplicate styles (e.g., use a common `ResourceDictionary` or shared stylesheet). For installer title/subtitle UI, move repeated subtitle typography into shared App-level styles (e.g., `App.xaml` or a shared `ResourceDictionary`) rather than duplicating per-view properties.
- Render numbered points in agreement/license text as **bold** on the installer agreement step.
- Render the disabled primary action on the agreement step so the button background looks like a secondary button without a border, and apply the disabled color only to the text using `#BEBCEA4D`.
- In the installer Folder step checkboxes, do not add extra leading check icon content; keep the default checkbox content and use checked color `#6357FF`.
- In the Complete step subtitle, when emphasizing a partial word or phrase, avoid relying on color-only emphasis; ensure the emphasized word uses an additional distinct visual style (e.g., bold, italic, underline, increased font weight, or a different background) that renders reliably.

### SVG Templates vs Canvas
- When an SVG template file is provided as a visual reference for a frame or panel background, **always build the shape first as an explicit Avalonia `Canvas` with `Path` and `Rectangle` elements**, transcribing the SVG path data directly. Use the `<Svg>` control to embed the file directly only as a last resort if a Canvas representation is not feasible.
- When transcribing SVG fills to Avalonia Canvas elements, map SVG attributes directly and separately — do not consolidate into a single hex-alpha value. For example: SVG `fill="black" fill-opacity="0.8"` → Avalonia `Fill="Black" Opacity="0.8"`.

### Uninstaller UI
- Implement a dedicated uninstaller as a UI-driven flow with its own `Views` and 3 steps: confirmation (cancel/uninstall), progress (deleting files), and completion (finish).
