import 'dart:math' as math;

import 'package:flutter/material.dart';

import 'kintsugi_palette.dart';

/// Builds the two themes from a [KintsugiPalette], in the HTW style (HTW-Infra/htw-style,
/// STYLE-GUIDE.md): Arial throughout, bold headings, uppercase slate labels, 999px pill buttons,
/// 6px fields, 12px cards, and one accent.
///
/// **The face is Liberation Sans, and that is Arial.** htw-style's font stack is
/// `Arial, …, "Liberation Sans", sans-serif`, a system font so nothing is downloaded — but Flutter
/// web paints into a canvas that cannot see system fonts, so "Arial" here would quietly land in
/// Flutter's own fallback. Liberation Sans is metric-compatible with Arial (the same advance
/// widths, so the same line breaks) and is in that stack; it and Liberation Mono are bundled
/// under assets/fonts/ (SIL OFL, licence beside them), so the UI fetches no font from anywhere.
/// Both are Regular and Bold only, as Arial is: the guide draws semibold as bold, and so does
/// Flutter when a w600 asks for a face that has no 600.
abstract final class AppTheme {
  static ThemeData light() => _build(KintsugiPalette.light, Brightness.light);

  static ThemeData dark() => _build(KintsugiPalette.dark, Brightness.dark);

  static const _sans = 'Liberation Sans';

  /// The family [mono] resolves to, for the one place that needs a family *string* rather than a
  /// [TextStyle]: xterm's `TerminalStyle`, in the remote terminal. It must name a face that is
  /// actually registered — on web there is no system monospace to fall back to, and a terminal in
  /// a proportional face breaks every aligned column and box-drawing TUI.
  /// `test/presentation/remote_shell_font_test.dart` checks the terminal uses it.
  static const monoFamily = 'Liberation Mono';

  /// The guide's uppercase label (`.htw-eyebrow`, table headers, badges, nav entries): bold,
  /// letter-spacing .06em, never smaller than 12px — the guide's own floor. Callers uppercase the
  /// text themselves; Flutter has no `text-transform`.
  static TextStyle label({
    required Color color,
    double size = 12,
    FontWeight weight = FontWeight.w700,
  }) {
    final px = math.max(size, 12.0);
    return TextStyle(
      fontFamily: _sans,
      color: color,
      fontSize: px,
      fontWeight: weight,
      letterSpacing: px * 0.06,
    );
  }

  /// The monospace face, for code and for field contents.
  static TextStyle mono({required Color color, double size = 13}) =>
      TextStyle(fontFamily: monoFamily, color: color, fontSize: size);

  static ThemeData _build(KintsugiPalette palette, Brightness brightness) {
    TextStyle sans(double size, Color color, [FontWeight weight = FontWeight.w400]) =>
        TextStyle(fontFamily: _sans, fontSize: size, color: color, fontWeight: weight, letterSpacing: 0);
    TextStyle heading(double size) => sans(size, palette.text, FontWeight.w700).copyWith(height: 1.2);

    final textTheme = TextTheme(
      // h1: the page title.
      headlineLarge: heading(26),
      // h2: a section heading within a page.
      titleLarge: heading(18),
      // h3.
      titleMedium: heading(15),
      titleSmall: heading(14),
      // Table headers and form labels share one slot: both are the guide's uppercase slate label,
      // and keeping them one style is what stops the two drifting apart.
      labelLarge: label(color: palette.muted),
      labelMedium: label(color: palette.muted),
        // 14px rather than the guide's 16px body, and the face's own line height rather than its 1.5:
      // these are dense admin tables, and 14px is the guide's own button and small-field size.
      bodyLarge: sans(15, palette.text),
      bodyMedium: sans(14, palette.text),
      // .htw-hint.
      bodySmall: sans(13, palette.muted),
    );

    const pill = WidgetStatePropertyAll<OutlinedBorder>(StadiumBorder());
    final focusRing = WidgetStateProperty.resolveWith<BorderSide?>(
      (states) => states.contains(WidgetState.focused) ? BorderSide(color: palette.focus, width: 2) : null,
    );

    return ThemeData(
      useMaterial3: true,
      brightness: brightness,
      fontFamily: _sans,
      scaffoldBackgroundColor: palette.background,
      canvasColor: palette.background,
      colorScheme: ColorScheme.fromSeed(seedColor: palette.action, brightness: brightness).copyWith(
        surface: palette.surface,
        onSurface: palette.text,
        onSurfaceVariant: palette.muted,
        primary: palette.action,
        onPrimary: palette.onAction,
        primaryContainer: palette.actionTint,
        onPrimaryContainer: palette.actionTintText,
        secondaryContainer: palette.actionTint,
        onSecondaryContainer: palette.actionTintText,
        error: palette.red,
        outline: palette.inputBorder,
        outlineVariant: palette.border,
        surfaceContainerLowest: palette.surface,
        surfaceContainerLow: palette.surface,
        surfaceContainer: palette.surface,
        surfaceContainerHigh: palette.surface,
        surfaceContainerHighest: palette.subtle,
        surfaceTint: Colors.transparent,
        shadow: palette.shadow.single.color,
      ),
      extensions: [palette],
      textTheme: textTheme,
      // The highlight behind selected text, for the app-wide SelectionArea in main.dart as well as
      // for text fields. Flutter's own fallback is a flat 50% grey, a smear on the dark page.
      textSelectionTheme: TextSelectionThemeData(
        selectionColor: palette.accentWash(brightness == Brightness.dark ? 0.35 : 0.22),
        cursorColor: palette.action,
        selectionHandleColor: palette.action,
      ),
      focusColor: palette.focus.withValues(alpha: 0.24),
      hoverColor: palette.accentWash(0.06),
      dividerTheme: DividerThemeData(color: palette.border, thickness: 1, space: 1),
      iconTheme: IconThemeData(color: palette.muted, size: 18),
      // The guide's buttons, for any Material button a screen uses directly. PrimaryButton and
      // SecondaryButton in core/widgets are the usual way in and set the same shape.
      filledButtonTheme: FilledButtonThemeData(
        style: ButtonStyle(
          shape: pill,
          side: focusRing,
          textStyle: WidgetStatePropertyAll(sans(14, palette.onAction, FontWeight.w700)),
        ),
      ),
      outlinedButtonTheme: OutlinedButtonThemeData(
        style: ButtonStyle(
          shape: pill,
          textStyle: WidgetStatePropertyAll(sans(14, palette.muted, FontWeight.w700)),
        ),
      ),
      textButtonTheme: TextButtonThemeData(
        style: ButtonStyle(
          shape: pill,
          foregroundColor: WidgetStatePropertyAll(palette.action),
          textStyle: WidgetStatePropertyAll(sans(14, palette.action, FontWeight.w700)),
        ),
      ),
      segmentedButtonTheme: SegmentedButtonThemeData(
        style: ButtonStyle(
          shape: pill,
          side: WidgetStatePropertyAll(BorderSide(color: palette.inputBorder)),
          backgroundColor: WidgetStateProperty.resolveWith(
            (s) => s.contains(WidgetState.selected) ? palette.action : palette.surface,
          ),
          foregroundColor: WidgetStateProperty.resolveWith(
            (s) => s.contains(WidgetState.selected) ? palette.onAction : palette.muted,
          ),
          textStyle: WidgetStatePropertyAll(sans(13, palette.muted, FontWeight.w700)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        isDense: true,
        filled: true,
        fillColor: palette.surface,
        hintStyle: sans(14, palette.muted),
        labelStyle: sans(13, palette.muted, FontWeight.w700),
        contentPadding: const EdgeInsets.symmetric(horizontal: 12, vertical: 10),
        border: _fieldBorder(palette.inputBorder),
        enabledBorder: _fieldBorder(palette.inputBorder),
        disabledBorder: _fieldBorder(palette.border),
        focusedBorder: _fieldBorder(palette.focus, 2),
        errorBorder: _fieldBorder(palette.red),
        focusedErrorBorder: _fieldBorder(palette.red, 2),
      ),
      checkboxTheme: CheckboxThemeData(
        fillColor: WidgetStateProperty.resolveWith(
          (states) => states.contains(WidgetState.selected) ? palette.action : Colors.transparent,
        ),
        checkColor: WidgetStatePropertyAll(palette.onAction),
        side: BorderSide(color: palette.inputBorder, width: 1.5),
        shape: const RoundedRectangleBorder(borderRadius: BorderRadius.all(Radius.circular(4))),
      ),
      radioTheme: RadioThemeData(fillColor: WidgetStatePropertyAll(palette.action)),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith(
          (s) => s.contains(WidgetState.selected) ? palette.onAction : palette.inputBorder,
        ),
        trackColor: WidgetStateProperty.resolveWith(
          (s) => s.contains(WidgetState.selected) ? palette.action : palette.subtle,
        ),
        trackOutlineColor: WidgetStateProperty.resolveWith(
          (s) => s.contains(WidgetState.selected) ? palette.action : palette.inputBorder,
        ),
      ),
      cardTheme: CardThemeData(
        color: palette.surface,
        surfaceTintColor: Colors.transparent,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(12),
          side: BorderSide(color: palette.border),
        ),
      ),
      // .modal: 12px, no border, the overlay shadow, the backdrop ink at 45%.
      dialogTheme: DialogThemeData(
        backgroundColor: palette.surface,
        surfaceTintColor: Colors.transparent,
        barrierColor: const Color(0x730F172A),
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12)),
        titleTextStyle: heading(18),
        contentTextStyle: sans(14, palette.text),
      ),
      // Dropdown menus: 8px, the overlay shadow.
      popupMenuTheme: PopupMenuThemeData(
        color: palette.surface,
        surfaceTintColor: Colors.transparent,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(8),
          side: BorderSide(color: palette.border),
        ),
        textStyle: sans(14, palette.text),
      ),
      menuTheme: MenuThemeData(
        style: MenuStyle(
          backgroundColor: WidgetStatePropertyAll(palette.surface),
          surfaceTintColor: const WidgetStatePropertyAll(Colors.transparent),
          shape: WidgetStatePropertyAll(
            RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(8),
              side: BorderSide(color: palette.border),
            ),
          ),
        ),
      ),
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: brightness == Brightness.dark ? palette.subtle : palette.text,
          borderRadius: BorderRadius.circular(6),
        ),
        textStyle: sans(12.5, brightness == Brightness.dark ? palette.text : palette.surface),
      ),
      progressIndicatorTheme: ProgressIndicatorThemeData(
        color: palette.action,
        linearTrackColor: palette.subtle,
        circularTrackColor: Colors.transparent,
      ),
      scrollbarTheme: ScrollbarThemeData(
        thumbColor: WidgetStatePropertyAll(palette.muted.withValues(alpha: 0.45)),
        radius: const Radius.circular(999),
      ),
      snackBarTheme: SnackBarThemeData(
        backgroundColor: palette.text,
        contentTextStyle: sans(14, palette.surface),
        behavior: SnackBarBehavior.floating,
        shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(10)),
      ),
    );
  }

  static OutlineInputBorder _fieldBorder(Color color, [double width = 1]) => OutlineInputBorder(
        borderRadius: BorderRadius.circular(6),
        borderSide: BorderSide(color: color, width: width),
      );
}
