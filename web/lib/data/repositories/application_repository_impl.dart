import '../../core/network/api_client.dart';
import '../../core/network/api_exception.dart';
import '../../domain/entities/application.dart';
import '../../domain/repositories/repositories.dart';
import '../models/application_mapper.dart';

class ApplicationRepositoryImpl implements ApplicationRepository {
  const ApplicationRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<ApplicationOverview> overview() async => applicationOverviewFromJson(
        await _api.getJson('/api/admin/applications') as Map<String, dynamic>,
      );

  /// Reads `UpgradePathScriptDto`. A 404 is a row whose script went since the overview was read.
  @override
  Future<String?> script({required String applicationName, required String platform}) async {
    try {
      final json = await _api.getJson(
        '/api/admin/applications/script',
        query: {'applicationName': applicationName, 'platform': platform},
      ) as Map<String, dynamic>;
      return json['script'] as String?;
    } on ApiException catch (e) {
      if (e.statusCode == 404) return null;
      rethrow;
    }
  }

  @override
  Future<ForcedPatchRunResult> forcePatchRuns({
    required String applicationName,
    required String platform,
    required List<String> hostNames,
  }) async =>
      forcedPatchRunResultFromJson(
        await _api.postJson('/api/admin/applications/forced-patch-runs', body: {
          'applicationName': applicationName,
          'platform': platform,
          'hostNames': hostNames,
        }) as Map<String, dynamic>,
      );
}
