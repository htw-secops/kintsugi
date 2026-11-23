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

/// A company can pick any colour, so the accent has to stay readable whatever it picks.
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
    test('accent "${entry.key}" gives text colours of at least 4.5:1 on both themes', () {
      for (final palette in [
        KintsugiPalette.dark.withAccent(entry.value),
        KintsugiPalette.light.withAccent(entry.value),
      ]) {
        final colours = {'neon': palette.neon, 'neonSoft': palette.neonSoft, 'neonDim': palette.neonDim};
        for (final colour in colours.entries) {
          expect(
            _contrast(colour.value, palette.background),
            greaterThanOrEqualTo(4.5),
            reason: '${colour.key} of ${palette.glowsEnabled ? "dark" : "light"} with ${entry.key}',
          );
        }
      }
    });
  }

  test('only the accent family changes', () {
    final tinted = KintsugiPalette.light.withAccent(const Color(0xFF3B82F6));
    expect(tinted.background, KintsugiPalette.light.background);
    expect(tinted.text, KintsugiPalette.light.text);
    expect(tinted.red, KintsugiPalette.light.red);
    expect(tinted.neon, isNot(KintsugiPalette.light.neon));
  });

  test('the default theme is the original palette, not a re-derived one', () {
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
