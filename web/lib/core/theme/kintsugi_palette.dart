import 'package:flutter/material.dart';

/// The colour tokens the whole UI is built from, carried on [ThemeData] as an extension.
///
/// **Every value here is an HTW token, and none is chosen here.** Kintsugi takes the HTW style
/// (HTW-Infra/htw-style) like every other HTW app, but Flutter paints to a canvas and cannot load
/// CSS — so instead of linking `htw.css` it vendors it into `web/htw/htw.css` (never served,
/// never edited; re-synced with htw-style's `scripts/sync-to-app.sh`) and mirrors the tokens
/// below. `test/core/htw_style_test.dart` reads that vendored file and fails if any field here
/// differs from the token named beside it, so a re-sync that moves a colour fails CI until this
/// file follows. To change a colour, change htw-style; never edit a value here alone.
///
/// The light theme is htw.css's `:root` tokens; the dark theme is its `--htw-dark-*` tokens
/// (htw-style 2.2.0), which come from the HTW Design System's own `.dark` block.
///
/// Widgets read tokens from here rather than hard-coding a colour so that the dark theme is a
/// single palette swap. A widget that reaches for a literal colour only works in one theme, and
/// the bug does not show up until someone toggles.
@immutable
class KintsugiPalette extends ThemeExtension<KintsugiPalette> {
  const KintsugiPalette({
    required this.background,
    required this.surface,
    required this.subtle,
    required this.nav,
    required this.navLine,
    required this.onNav,
    required this.navMuted,
    required this.navPill,
    required this.navPillText,
    required this.text,
    required this.muted,
    required this.border,
    required this.inputBorder,
    required this.action,
    required this.actionHover,
    required this.onAction,
    required this.actionTint,
    required this.actionTintText,
    required this.focus,
    required this.green,
    required this.greenTint,
    required this.amber,
    required this.amberTint,
    required this.red,
    required this.redTint,
    required this.danger,
    required this.chart,
    required this.shadow,
  });

  /// The page. Cards, fields and menus are [surface] on it.
  final Color background;
  final Color surface;

  /// Subtle fills: a neutral badge, a disabled button, a table header.
  final Color subtle;

  /// The sidebar, its Cyan 500 accent line, its label colour, its secondary text (the version,
  /// the signed-in name), and the pill the current page sits in with that pill's label.
  final Color nav;
  final Color navLine;
  final Color onNav;
  final Color navMuted;
  final Color navPill;
  final Color navPillText;

  /// Headings and body text, and the slate for labels, hints and anything secondary.
  final Color text;
  final Color muted;

  /// Every hairline, and the boundary of a field, checkbox or radio — the second passes 3:1
  /// (WCAG 1.4.11) where a hairline need not.
  final Color border;
  final Color inputBorder;

  /// The one accent: every button fill, link, checked control and current item. [actionHover] is
  /// its pressed fill, [onAction] the label on it — white in light, dark ink on the dark theme's
  /// sky fill, where white would be 2.48:1.
  final Color action;
  final Color actionHover;
  final Color onAction;
  final Color actionTint;
  final Color actionTintText;

  /// Every keyboard focus ring.
  final Color focus;

  /// Status text colours, each with the tint it sits on in a badge. All pass 4.5:1 as text on
  /// the page, on a surface and on their own tint.
  final Color green;
  final Color greenTint;
  final Color amber;
  final Color amberTint;
  final Color red;
  final Color redTint;

  /// The danger button's fill (white label, 4.83:1). Kept in the dark theme too.
  final Color danger;

  /// The five categorical chart series (htw-style --chart-1..5): every pair distinct for normal
  /// vision and for deuteranopes. The same five in both themes — the DS's dark chart tokens are a
  /// blue ramp that fails that check, and these sit in a lightness band that reads on either ground.
  final List<Color> chart;

  /// Card elevation: slate-tinted in light, nav-tinted in dark, never plain black.
  final List<BoxShadow> shadow;

  /// htw.css `:root`.
  static const light = KintsugiPalette(
    background: Color(0xFFF8FAFC), // --htw-neutral-50
    surface: Color(0xFFFFFFFF), // white
    subtle: Color(0xFFF1F5F9), // --htw-neutral-100
    nav: Color(0xFF05193A), // --htw-navy
    navLine: Color(0xFF00AEEF), // --htw-logo-cyan
    onNav: Color(0xFFFFFFFF), // white
    navMuted: Color(0xFFA7E5FB), // --htw-cyan-100
    navPill: Color(0xFFFFFFFF), // white
    navPillText: Color(0xFF0070C0), // --htw-action-text
    text: Color(0xFF0F172A), // --htw-ink
    muted: Color(0xFF475569), // --htw-slate
    border: Color(0xFFE2E8F0), // --htw-neutral-200
    inputBorder: Color(0xFF64748B), // --htw-input-border
    action: Color(0xFF0070C0), // --htw-action
    actionHover: Color(0xFF005C9E), // --htw-action-hover
    onAction: Color(0xFFFFFFFF), // --htw-action-on
    actionTint: Color(0xFFE6F3FC), // --htw-action-tint
    actionTintText: Color(0xFF005C9E), // --htw-action-tint-text
    focus: Color(0xFF0091D6), // --htw-cyan-mid
    green: Color(0xFF065F46), // --htw-success-text
    greenTint: Color(0xFFD1FAE5), // --htw-success-tint
    amber: Color(0xFF92400E), // --htw-warning-text
    amberTint: Color(0xFFFEF3C7), // --htw-warning-tint
    red: Color(0xFF991B1B), // --htw-destructive-text
    redTint: Color(0xFFFEE2E2), // --htw-destructive-tint
    danger: Color(0xFFDC2626), // --htw-danger
    chart: _chart,
    shadow: [BoxShadow(color: Color(0x140F172A), blurRadius: 4, offset: Offset(0, 2))], // --shadow-1
  );

  /// htw.css `--htw-dark-*`.
  static const dark = KintsugiPalette(
    background: Color(0xFF101822), // --htw-dark-page
    surface: Color(0xFF19232F), // --htw-dark-surface
    subtle: Color(0xFF24303D), // --htw-dark-subtle
    nav: Color(0xFF09111A), // --htw-dark-nav
    navLine: Color(0xFF00AEEF), // --htw-logo-cyan
    onNav: Color(0xFFF0F5F9), // --htw-dark-ink
    navMuted: Color(0xFFA7E5FB), // --htw-cyan-100
    navPill: Color(0xFF293D52), // --htw-dark-action-tint
    navPillText: Color(0xFF93D7FF), // --htw-dark-action-tint-text
    text: Color(0xFFF0F5F9), // --htw-dark-ink
    muted: Color(0xFF99A7B3), // --htw-dark-slate
    border: Color(0x1FFFFFFF), // --htw-dark-hairline
    inputBorder: Color(0xFF64748B), // --htw-dark-input-border
    action: Color(0xFF00B0F0), // --htw-dark-action
    actionHover: Color(0xFF06A1DC), // --htw-dark-action-hover
    onAction: Color(0xFF0A1420), // --htw-dark-action-on
    actionTint: Color(0xFF293D52), // --htw-dark-action-tint
    actionTintText: Color(0xFF93D7FF), // --htw-dark-action-tint-text
    focus: Color(0xFF059FE3), // --htw-dark-focus
    green: Color(0xFF34D399), // --htw-dark-success-text
    greenTint: Color(0xFF064E3B), // --htw-dark-success-tint
    amber: Color(0xFFFBBF24), // --htw-dark-warning-text
    amberTint: Color(0xFF451A03), // --htw-dark-warning-tint
    red: Color(0xFFF87171), // --htw-dark-destructive-text
    redTint: Color(0xFF450A0A), // --htw-dark-destructive-tint
    danger: Color(0xFFDC2626), // --htw-danger
    chart: _chart,
    shadow: [BoxShadow(color: Color(0x8009111A), blurRadius: 4, offset: Offset(0, 2))], // --htw-dark-shadow-1
  );

  static const _chart = [
    Color(0xFF009FE3), // --chart-1
    Color(0xFFEE6438), // --chart-2
    Color(0xFF6250D6), // --chart-3
    Color(0xFF1BAF7A), // --chart-4
    Color(0xFFDBA91B), // --chart-5
  ];

  /// A translucent wash of the accent, for hover and selection tints.
  Color accentWash(double opacity) => action.withValues(alpha: opacity);

  /// The tint a badge in [color] sits on — one of the status colours above, or the accent.
  Color tintFor(Color color) {
    if (color == green) return greenTint;
    if (color == amber) return amberTint;
    if (color == red) return redTint;
    if (color == action) return actionTint;
    return subtle;
  }

  /// The label colour on [tintFor]'s tint: the accent's tint takes its darker text.
  Color onTintFor(Color color) => color == action ? actionTintText : color;

  /// The colour a status chip uses, keyed by the status strings the API sends.
  ///
  /// Keyed on the server's own `statusKey` (see `UpgradePathSummaryDto.StatusKey`) and on
  /// `HostStatus`, so a status the server adds shows up as [muted] rather than as a crash.
  Color forStatusKey(String statusKey) => switch (statusKey) {
        'up-to-date' || 'online' => green,
        'update-available' || 'review-sign' => amber,
        // 'patch-failed' is the Failed Updates screen's outstanding row — the same red a failed
        // update check gets, because both mean "this is not working right now".
        'check-failed' || 'offline' || 'patch-failed' => red,
        'patch-succeeded' => green,
        // The "OS update" tag on a Failed Updates row: it names what kind of row it is, not how it
        // is doing, so it takes the accent rather than a status colour.
        'os-update' => action,
        'not-found' || 'decommissioned' => amber,
        // The Vulnerabilities screen. 'exploited' is its own key rather than reusing
        // 'cve-critical', because CISA listing a CVE as actively exploited is a different claim
        // from a high CVSS score and the screen sorts on it first — a 5.5 being exploited outranks
        // a 9.8 that is not.
        'exploited' || 'cve-critical' => red,
        'cve-high' => amber,
        'cve-medium' || 'mapping-suggested' => amber,
        'mapping-confirmed' => green,
        // Deliberately muted rather than red: an unmapped subject is missing coverage, which the
        // screen states as a count, not a fault to alarm about on every row.
        'cve-low' || 'mapping-unmapped' || 'mapping-not-applicable' => muted,
        // Another tool's: in the accent colour, which is a plain fact about the row and neither the
        // amber of something to do nor the red of something wrong.
        'managed-elsewhere' => action,
        _ => muted,
      };

  @override
  KintsugiPalette copyWith({
    Color? background,
    Color? surface,
    Color? subtle,
    Color? nav,
    Color? navLine,
    Color? onNav,
    Color? navMuted,
    Color? navPill,
    Color? navPillText,
    Color? text,
    Color? muted,
    Color? border,
    Color? inputBorder,
    Color? action,
    Color? actionHover,
    Color? onAction,
    Color? actionTint,
    Color? actionTintText,
    Color? focus,
    Color? green,
    Color? greenTint,
    Color? amber,
    Color? amberTint,
    Color? red,
    Color? redTint,
    Color? danger,
    List<Color>? chart,
    List<BoxShadow>? shadow,
  }) =>
      KintsugiPalette(
        background: background ?? this.background,
        surface: surface ?? this.surface,
        subtle: subtle ?? this.subtle,
        nav: nav ?? this.nav,
        navLine: navLine ?? this.navLine,
        onNav: onNav ?? this.onNav,
        navMuted: navMuted ?? this.navMuted,
        navPill: navPill ?? this.navPill,
        navPillText: navPillText ?? this.navPillText,
        text: text ?? this.text,
        muted: muted ?? this.muted,
        border: border ?? this.border,
        inputBorder: inputBorder ?? this.inputBorder,
        action: action ?? this.action,
        actionHover: actionHover ?? this.actionHover,
        onAction: onAction ?? this.onAction,
        actionTint: actionTint ?? this.actionTint,
        actionTintText: actionTintText ?? this.actionTintText,
        focus: focus ?? this.focus,
        green: green ?? this.green,
        greenTint: greenTint ?? this.greenTint,
        amber: amber ?? this.amber,
        amberTint: amberTint ?? this.amberTint,
        red: red ?? this.red,
        redTint: redTint ?? this.redTint,
        danger: danger ?? this.danger,
        chart: chart ?? this.chart,
        shadow: shadow ?? this.shadow,
      );

  @override
  KintsugiPalette lerp(KintsugiPalette? other, double t) {
    if (other == null) return this;
    Color l(Color a, Color b) => Color.lerp(a, b, t)!;
    return KintsugiPalette(
      background: l(background, other.background),
      surface: l(surface, other.surface),
      subtle: l(subtle, other.subtle),
      nav: l(nav, other.nav),
      navLine: l(navLine, other.navLine),
      onNav: l(onNav, other.onNav),
      navMuted: l(navMuted, other.navMuted),
      navPill: l(navPill, other.navPill),
      navPillText: l(navPillText, other.navPillText),
      text: l(text, other.text),
      muted: l(muted, other.muted),
      border: l(border, other.border),
      inputBorder: l(inputBorder, other.inputBorder),
      action: l(action, other.action),
      actionHover: l(actionHover, other.actionHover),
      onAction: l(onAction, other.onAction),
      actionTint: l(actionTint, other.actionTint),
      actionTintText: l(actionTintText, other.actionTintText),
      focus: l(focus, other.focus),
      green: l(green, other.green),
      greenTint: l(greenTint, other.greenTint),
      amber: l(amber, other.amber),
      amberTint: l(amberTint, other.amberTint),
      red: l(red, other.red),
      redTint: l(redTint, other.redTint),
      danger: l(danger, other.danger),
      chart: [for (var i = 0; i < chart.length; i++) l(chart[i], other.chart[i])],
      shadow: BoxShadow.lerpList(shadow, other.shadow, t)!,
    );
  }
}

/// Reads the palette off the nearest theme.
extension KintsugiPaletteContext on BuildContext {
  KintsugiPalette get palette =>
      Theme.of(this).extension<KintsugiPalette>() ?? KintsugiPalette.light;
}
