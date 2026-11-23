import 'dart:convert';

import '../../core/network/api_client.dart';
import '../../domain/entities/appearance.dart';
import '../../domain/repositories/repositories.dart';

/// `GET` and `PUT /api/admin/settings/appearance`: `{colourMode, presetKey, accentHex, wallpaper, logoAnimation}`,
/// the wallpaper as base64 (how System.Text.Json writes a `byte[]`) or null.
class AppearanceSettingsRepositoryImpl implements AppearanceSettingsRepository {
  const AppearanceSettingsRepositoryImpl(this._api);

  final ApiClient _api;

  static const _path = '/api/admin/settings/appearance';

  @override
  Future<AppearanceSettings> read() async => appearanceFromJson(await _api.getJson(_path) as Map<String, dynamic>);

  @override
  Future<AppearanceSettings> update(AppearanceSettings settings) async => appearanceFromJson(
        await _api.putJson(_path, body: {
          'colourMode': settings.colourMode,
          'presetKey': settings.presetKey,
          'accentHex': settings.accentHex,
          'wallpaper': settings.wallpaper == null ? null : base64Encode(settings.wallpaper!),
          'logoAnimation': settings.logoAnimation,
        }) as Map<String, dynamic>,
      );
}

AppearanceSettings appearanceFromJson(Map<String, dynamic> json) => AppearanceSettings(
      colourMode: json['colourMode'] as String? ?? AppearanceSettings.defaults.colourMode,
      presetKey: json['presetKey'] as String? ?? AppearanceSettings.defaults.presetKey,
      accentHex: json['accentHex'] as String?,
      logoAnimation: json['logoAnimation'] as String? ?? AppearanceSettings.defaults.logoAnimation,
      wallpaper: json['wallpaper'] is String && (json['wallpaper'] as String).isNotEmpty
          ? base64Decode(json['wallpaper'] as String)
          : null,
    );
