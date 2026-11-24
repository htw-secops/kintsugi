import 'package:flutter/painting.dart';

/// A ready-made accent colour a company can pick. Only the accent changes: backgrounds, text and
/// the status colours (green, amber, red) keep their meaning in every theme.
class ThemePreset {
  const ThemePreset(this.key, this.name, this.accent);

  final String key;
  final String name;

  /// Null is the original Kintsugi cyan, left exactly as it is in [KintsugiPalette] rather than
  /// re-derived, so choosing it is no change at all.
  final Color? accent;
}

const themePresets = <ThemePreset>[
  ThemePreset('kintsugi', 'HTW blue', null),
  ThemePreset('gold', 'Gold', Color(0xFFF2B01E)),
  ThemePreset('ocean', 'Ocean', Color(0xFF3B82F6)),
  ThemePreset('forest', 'Forest', Color(0xFF22C55E)),
  ThemePreset('coral', 'Coral', Color(0xFFFF6B4A)),
];

/// The accent a company's settings ask for: their own colour if it is a valid one, else the
/// preset's. Null means the original palette.
Color? accentFor(String presetKey, String? accentHex) {
  final custom = parseAccentHex(accentHex);
  if (custom != null) return custom;
  for (final preset in themePresets) {
    if (preset.key == presetKey) return preset.accent;
  }
  return null;
}

final _hex = RegExp(r'^#?([0-9a-fA-F]{6})$');

/// `#RRGGBB` (the `#` is optional) as an opaque colour, or null for anything else.
Color? parseAccentHex(String? text) {
  final match = _hex.firstMatch(text?.trim() ?? '');
  return match == null ? null : Color(0xFF000000 | int.parse(match.group(1)!, radix: 16));
}
