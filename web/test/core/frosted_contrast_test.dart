import 'package:flutter/painting.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/kintsugi_palette.dart';
import 'package:kintsugi_web/core/widgets/frosted.dart';

/// Text on frosted glass must read whatever picture is behind it. A picture can be no darker than black or
/// lighter than white, and contrast moves one way with the background, so those two are the worst cases.
void main() {
  Color glassOver(KintsugiPalette palette, Color picture) {
    final bg = palette.background;
    final covered = Color.alphaBlend(bg.withValues(alpha: frostedCover), picture);
    return Color.alphaBlend(bg.withValues(alpha: frostedAlpha(palette)), covered);
  }

  final palettes = {
    'light': KintsugiPalette.light,
    'dark': KintsugiPalette.dark,
    'light, pale yellow accent': KintsugiPalette.light.withAccent(const Color(0xFFFFE066)),
    'dark, navy accent': KintsugiPalette.dark.withAccent(const Color(0xFF001F54)),
  };

  for (final entry in palettes.entries) {
    for (final (name, picture) in [('black', const Color(0xFF000000)), ('white', const Color(0xFFFFFFFF))]) {
      test('${entry.key} theme over a $name picture keeps text, muted text and the accent at 4.5:1', () {
        final glass = glassOver(entry.value, picture);
        for (final ink in [entry.value.text, entry.value.muted, entry.value.action]) {
          expect(KintsugiPalette.contrastRatio(ink, glass), greaterThanOrEqualTo(frostedMinContrast));
        }
      });
    }
  }

  test('the glass is never thinner than the 60% a mid-tone photo was tested at, and never more than solid', () {
    for (final palette in palettes.values) {
      expect(frostedAlpha(palette), inInclusiveRange(0.60, 1.0));
    }
  });
}
