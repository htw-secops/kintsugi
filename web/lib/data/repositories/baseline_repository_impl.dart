import '../../core/network/api_client.dart';
import '../../domain/entities/baseline.dart';
import '../../domain/repositories/repositories.dart';
import '../models/baseline_mapper.dart';

/// Talks to `/api/admin/baselines` (`AdminBaselinesController`).
class BaselineRepositoryImpl implements BaselineRepository {
  const BaselineRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/baselines';

  @override
  Future<List<BaselineSummary>> list() async =>
      ((await _api.getJson(_base)) as List).whereType<Map<String, dynamic>>().map(baselineSummaryFromJson).toList();

  @override
  Future<BaselineDetail> get(String baselineId) async =>
      baselineDetailFromJson(await _api.getJson('$_base/${Uri.encodeComponent(baselineId)}') as Map<String, dynamic>);

  @override
  Future<BaselineHostDetail> host(String baselineId, String hostId) async => baselineHostDetailFromJson(
        await _api.getJson('$_base/${Uri.encodeComponent(baselineId)}/hosts/${Uri.encodeComponent(hostId)}')
            as Map<String, dynamic>,
      );

  @override
  Future<List<BaselinePersonRow>> people(String baselineId) async =>
      ((await _api.getJson('$_base/${Uri.encodeComponent(baselineId)}/people')) as List)
          .whereType<Map<String, dynamic>>()
          .map(baselinePersonRowFromJson)
          .toList();

  /// An absolute route on the same controller, so it carries the same `[RequireAdminSession]`.
  @override
  Future<List<String>> installedApplicationNames(String query) async =>
      ((await _api.getJson('/api/admin/installed-application-names?query=${Uri.encodeQueryComponent(query)}'))
              as List)
          .whereType<String>()
          .toList();

  @override
  Future<void> create(Map<String, dynamic> body) => _api.postJson(_base, body: body);

  @override
  Future<void> addVersion(String baselineId, Map<String, dynamic> body) =>
      _api.postJson('$_base/${Uri.encodeComponent(baselineId)}/versions', body: {'items': body['items']});

  @override
  Future<void> sign(String versionId, String documentSha256) => _api.postJson(
        '$_base/versions/${Uri.encodeComponent(versionId)}/sign',
        body: {'documentSha256': documentSha256},
      );

  @override
  Future<void> assign(
    String baselineId, {
    required bool allWindowsHosts,
    required List<String> hostNames,
    String? applicationName,
  }) =>
      _api.putJson(
        '$_base/${Uri.encodeComponent(baselineId)}/assignment',
        // `applicationName` mirrors AssignBaselineRequest.ApplicationName; null clears it.
        body: {'allWindowsHosts': allWindowsHosts, 'hostNames': hostNames, 'applicationName': applicationName},
      );
}
