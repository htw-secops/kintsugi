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
  });

  /// What nothing stored means. Dark and the original cyan are what the product shipped with, so a
  /// company that never opens this screen sees no change.
  static const defaults = AppearanceSettings();

  /// `light`, `dark` or `system`. A string, as the API sends it, so a mode a newer server adds
  /// falls back to dark in the theme rather than failing to parse.
  final String colourMode;

  /// A key from `themePresets`. An unknown key means the original palette.
  final String presetKey;

  /// `#RRGGBB`, or null for "use the preset". Wins over [presetKey] when set.
  final String? accentHex;

  AppearanceSettings copyWith({String? colourMode, String? presetKey, String? accentHex, bool clearAccent = false}) =>
      AppearanceSettings(
        colourMode: colourMode ?? this.colourMode,
        presetKey: presetKey ?? this.presetKey,
        accentHex: clearAccent ? null : (accentHex ?? this.accentHex),
      );

  @override
  List<Object?> get props => [colourMode, presetKey, accentHex];
}
