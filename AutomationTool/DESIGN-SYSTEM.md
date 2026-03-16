# IdolClick Design System

Last updated: 2026-03-13

This document defines the UI theming architecture used by IdolClick and the rules for extending it without creating more one-off styling.

## Goals

- Support future skins and theme variants without rewriting window XAML.
- Separate base tokens from component styles.
- Reduce hardcoded colors, font choices, spacing, and repeated control templates.
- Keep WPF-UI as the host design framework while preserving IdolClick-specific visual identity.

## Current Structure

- `App.xaml` merges WPF-UI dictionaries first, then `UI/ThemeResources.xaml`.
- `UI/ThemeResources.xaml` is now an umbrella dictionary.
- `UI/DesignSystem/DesignTokens.xaml` owns color, brush, typography, spacing, radius, and semantic workspace tokens.
- `UI/DesignSystem/ComponentStyles.xaml` owns reusable control styles and templates.

## Token Layers

### Foundation tokens

These are the lowest-level visual primitives:

- Base colors: `PrimaryColor`, `SurfaceColor`, `TextColor`, `ErrorColor`
- Typography: `AppFont`, `HeadingFont`, `MonoFont`
- Spacing and radius: `SpacingSm`, `SpacingLg`, `RadiusMd`, `RadiusPill`

### Semantic tokens

These describe intent rather than implementation detail:

- Workspace accents: `WorkspaceClassicBrush`, `WorkspaceReasonBrush`, `WorkspaceTeachBrush`, `WorkspaceCaptureBrush`
- Overlay and shell surfaces: `OverlayBackdropBrush`, `PanelOverlayBrush`, `WindowGlassBrush`, `OrbShellBrush`, `OrbCoreBrush`
- Shared interaction states: `SurfaceInteractiveBrush`, `ActivityBarHoverBrush`

## Rules for Future UI Work

1. Do not add raw hex colors to new production XAML when an existing token can express the intent.
2. If a new visual concept appears in more than one screen, add a semantic token before adding a local brush.
3. If a custom button, card, or panel template appears twice, move it into `ComponentStyles.xaml`.
4. Keep window-specific experimental styling local only until it is reused or validated.
5. Use semantic workspace brushes for mode accents instead of embedding per-screen color literals.

## Known Debt

- Several windows still use inline `FontSize`, `FontFamily`, and hardcoded overlay colors.
- The in-flight Capture workspace introduces new custom visual surfaces that should migrate to semantic tokens after merge.
- `SplashWindow.xaml` and `RegionSelectorOverlay.xaml` still carry isolated color treatment outside the design-token system.

## Recommended Next Refactors

1. Migrate overlay windows to semantic overlay tokens.
2. Replace repeated inline typography values with token-based styles for headings, body, and metadata.
3. Add light/dark skin support by swapping token dictionaries rather than editing component templates.
4. Introduce a small set of reusable card variants for launcher, capture gallery, and status panels.