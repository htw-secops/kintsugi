import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/kintsugi_palette.dart';
import 'package:kintsugi_web/core/theme/theme_presets.dart';

double _contrast(Color a, Color b) {
  final hi = math.max(a.computeLuminance(), b.computeLuminance());
  final lo = math.min(a.computeLuminance(), b.computeLuminance());
  return (hi + 0.05) / (lo + 0.05);
}

/// A company can pick any colour, so the action colours have to stay readable whatever it picks, and
/// nothing but the action colours may move (htw-style: an app "may set the --htw-action-* tokens, and
/// nothing else").
void main() {
  final accents = <String, Color>{
    for (final preset in themePresets)
      if (preset.accent != null) preset.key: preset.accent!,
    'pale yellow': const Color(0xFFFFFF99),
    'pure white': const Color(0xFFFFFFFF),
    'pure black': const Color(0xFF000000),
    'navy': const Color(0xFF000080),
    'grey': const Color(0xFF808080),
  };

  for (final entry in accents.entries) {
    test('accent "${entry.key}" keeps the action colours readable on both themes', () {
      for (final (name, base) in [('light', KintsugiPalette.light), ('dark', KintsugiPalette.dark)]) {
        final p = base.withAccent(entry.value);
        final reason = '$name with ${entry.key}';
        // The action is a button fill, link text and a highlight: it must read on the page and the card.
        expect(_contrast(p.action, p.surface), greaterThanOrEqualTo(4.5), reason: 'action on surface, $reason');
        expect(_contrast(p.action, p.background), greaterThanOrEqualTo(4.5), reason: 'action on page, $reason');
        expect(_contrast(p.onAction, p.action), greaterThanOrEqualTo(4.5), reason: 'text on the action, $reason');
        expect(_contrast(p.actionTintText, p.actionTint), greaterThanOrEqualTo(4.5), reason: 'tint text, $reason');
        expect(_contrast(p.navPillText, p.navPill), greaterThanOrEqualTo(4.5), reason: 'nav pill text, $reason');
        expect(p.actionHover, isNot(p.action), reason: 'hover must differ from the fill, $reason');
      }
    });
  }

  // Focus rings are keyboard users' way of seeing where they are, so they must not take a company's
  // colour (a yellow accent would put a yellow ring on a yellow button). The light ring is
  // --htw-cyan-mid; the dark one is htw-style's own dark focus colour.
  for (final entry in accents.entries) {
    test('accent "${entry.key}" leaves the focus ring as the style has it', () {
      for (final base in [KintsugiPalette.light, KintsugiPalette.dark]) {
        expect(base.withAccent(entry.value).focus, base.focus, reason: entry.key);
      }
      expect(KintsugiPalette.light.withAccent(entry.value).focus, const Color(0xFF0091D6), reason: entry.key);
    });
  }

  test('only the action colours change: every other colour is the style\'s own', () {
    for (final base in [KintsugiPalette.light, KintsugiPalette.dark]) {
      final p = base.withAccent(const Color(0xFFF2B01E));
      expect(p.action, isNot(base.action));
      expect(
        [p.background, p.surface, p.subtle, p.nav, p.navLine, p.onNav, p.navMuted, p.text, p.muted,
          p.border, p.inputBorder, p.focus, p.green, p.greenTint, p.amber, p.amberTint, p.red, p.redTint, p.danger],
        [base.background, base.surface, base.subtle, base.nav, base.navLine, base.onNav, base.navMuted,
          base.text, base.muted, base.border, base.inputBorder, base.focus, base.green, base.greenTint, base.amber,
          base.amberTint, base.red, base.redTint, base.danger],
      );
      expect(p.chart, base.chart);
      // The light theme's pill is the card colour; only the dark one is the action's tint.
      if (base.background.computeLuminance() > 0.5) expect(p.navPill, base.navPill);
    }
  });

  test('HTW blue, the default, is the style\'s own palette and not a re-derived one', () {
    expect(accentFor('kintsugi', null), isNull);
    expect(accentFor('nonsense', null), isNull);
  });

  test('a valid custom colour wins over the preset; an invalid one is ignored', () {
    expect(accentFor('gold', '#3B82F6'), const Color(0xFF3B82F6));
    expect(accentFor('gold', '3b82f6'), const Color(0xFF3B82F6));
    expect(accentFor('gold', '#12345'), themePresets[1].accent);
    expect(parseAccentHex('red'), isNull);
    expect(parseAccentHex(''), isNull);
  });
}
