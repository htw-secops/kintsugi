import '../../core/network/api_client.dart';
import '../../domain/entities/appearance.dart';
import '../../domain/repositories/repositories.dart';

/// `GET` and `PUT /api/admin/settings/appearance`: `{colourMode, presetKey, accentHex}`.
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
        }) as Map<String, dynamic>,
      );
}

AppearanceSettings appearanceFromJson(Map<String, dynamic> json) => AppearanceSettings(
      colourMode: json['colourMode'] as String? ?? AppearanceSettings.defaults.colourMode,
      presetKey: json['presetKey'] as String? ?? AppearanceSettings.defaults.presetKey,
      accentHex: json['accentHex'] as String?,
    );
