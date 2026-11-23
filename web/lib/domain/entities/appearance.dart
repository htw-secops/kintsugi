import 'dart:typed_data';

import 'package:equatable/equatable.dart';

/// How a company has chosen Kintsugi to look, for every administrator who signs in.
///
/// The organisation's name and logo are not here: they already live on the patching policy (the
/// agent's pop-ups use them), and a second copy would drift. The Appearance screen shows them and
/// links to where they are changed.
class AppearanceSettings extends Equatable {
  const AppearanceSettings({
    this.colourMode = 'dark',
    this.presetKey = 'kintsugi',
    this.accentHex,
    this.wallpaper,
    this.logoAnimation = 'spin',
  });

  /// What nothing stored means. Dark and the original cyan are what the product shipped with, so a
  /// company that never opens this screen sees no change.
  static const defaults = AppearanceSettings();

  /// Largest wallpaper accepted. It rides in every administrator's settings read as base64, so it is
  /// kept to what a good photo compresses to rather than what a camera produces.
  static const maxWallpaperBytes = 1024 * 1024;

  static const wallpaperTooLarge = 'The image must be 1 MB or smaller.';

  /// `light`, `dark` or `system`. A string, as the API sends it, so a mode a newer server adds
  /// falls back to dark in the theme rather than failing to parse.
  final String colourMode;

  /// A key from `themePresets`. An unknown key means the original palette.
  final String presetKey;

  /// `#RRGGBB`, or null for "use the preset". Wins over [presetKey] when set.
  final String? accentHex;

  /// How the sidebar's logo spot shows a company logo beside Kintsugi's own: `spin`, `fade` or `off`
  /// (Kintsugi's only). Does nothing when the company has no logo. Anything else reads as `spin`.
  final String logoAnimation;

  /// A PNG, JPEG or WebP drawn behind every screen, under a scrim that keeps text readable. Null
  /// for none.
  final Uint8List? wallpaper;

  /// Why [bytes] cannot be the wallpaper, or null when they can. The server has the last word.
  static String? wallpaperProblem(Uint8List bytes) {
    if (bytes.length > maxWallpaperBytes) return wallpaperTooLarge;
    bool starts(List<int> sig, [int at = 0]) {
      if (bytes.length < at + sig.length) return false;
      for (var i = 0; i < sig.length; i++) {
        if (bytes[at + i] != sig[i]) return false;
      }
      return true;
    }

    final png = starts(const [0x89, 0x50, 0x4E, 0x47]);
    final jpeg = starts(const [0xFF, 0xD8, 0xFF]);
    final webp = starts(const [0x52, 0x49, 0x46, 0x46]) && starts(const [0x57, 0x45, 0x42, 0x50], 8);
    return png || jpeg || webp ? null : 'The image must be a PNG, JPEG or WebP file.';
  }

  AppearanceSettings copyWith({
    String? colourMode,
    String? presetKey,
    String? accentHex,
    bool clearAccent = false,
    Uint8List? wallpaper,
    bool clearWallpaper = false,
    String? logoAnimation,
  }) =>
      AppearanceSettings(
        colourMode: colourMode ?? this.colourMode,
        presetKey: presetKey ?? this.presetKey,
        accentHex: clearAccent ? null : (accentHex ?? this.accentHex),
        wallpaper: clearWallpaper ? null : (wallpaper ?? this.wallpaper),
        logoAnimation: logoAnimation ?? this.logoAnimation,
      );

  @override
  List<Object?> get props => [colourMode, presetKey, accentHex, wallpaper, logoAnimation];
}
