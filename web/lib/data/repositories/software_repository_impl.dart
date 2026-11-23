import '../../core/network/api_client.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/software_title.dart';
import '../../domain/repositories/repositories.dart';
import '../models/software_mapper.dart';

/// Talks to `/api/admin/software` (`AdminSoftwareController`, `[RequireAdminSession]`).
///
/// Every call is documented to answer with the whole screen, `{titles, secrets}`. Each one still
/// falls back to a `GET` when the answer is not that shape — a `204` from a delete, say — because a
/// screen left showing the state from before a change it just made is worse than one extra round
/// trip.
class SoftwareRepositoryImpl implements SoftwareRepository {
  const SoftwareRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/software';

  static String _title(String id) => '$_base/${Uri.encodeComponent(id)}';

  static String _secret(String name) => '$_base/secrets/${Uri.encodeComponent(name)}';

  Future<SoftwareOverview> _overviewOr(Object? raw) async =>
      isSoftwareOverviewJson(raw) ? softwareOverviewFromJson(raw as Map<String, dynamic>) : read();

  @override
  Future<SoftwareOverview> read() async =>
      softwareOverviewFromJson(await _api.getJson(_base) as Map<String, dynamic>);

  @override
  Future<SoftwareOverview> create(SoftwareTitleDraft draft) async =>
      _overviewOr(await _api.postJson(_base, body: draft.toJson()));

  @override
  Future<SoftwareOverview> update(
    String id, {
    required SoftwareMode mode,
    String? detectAs,
    required List<String> secretNames,
  }) async =>
      _overviewOr(await _api.putJson(
        _title(id),
        body: {'mode': mode.wireName, 'detectAs': detectAs, 'secretNames': secretNames},
      ));

  @override
  Future<SoftwareOverview> assign(String id, {required bool allHosts, required List<String> hostNames}) async =>
      _overviewOr(await _api.putJson(
        '${_title(id)}/assignment',
        body: {'allHosts': allHosts, 'hostNames': hostNames},
      ));

  @override
  Future<SoftwareRunNowResult> runNow(String id) async {
    final raw = await _api.postJson('${_title(id)}/run-now');
    return softwareRunNowResultFromJson(raw is Map<String, dynamic> ? raw : const {});
  }

  @override
  Future<SoftwareOverview> delete(String id) async => _overviewOr(await _api.deleteJson(_title(id)));

  @override
  Future<SoftwareOverview> setSecret(String name, String value) async =>
      _overviewOr(await _api.putJson(_secret(name), body: {'value': value}));

  @override
  Future<SoftwareOverview> deleteSecret(String name) async => _overviewOr(await _api.deleteJson(_secret(name)));
}
