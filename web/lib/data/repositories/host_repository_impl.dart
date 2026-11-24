import '../../core/network/api_client.dart';
import '../../core/network/json_reader.dart';
import '../../domain/entities/host.dart';
import '../../domain/repositories/repositories.dart';
import '../models/host_mapper.dart';

class HostRepositoryImpl implements HostRepository {
  const HostRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<List<HostSummary>> list() async =>
      listFromJson(await _api.getJson('/api/hosts'), hostFromJson);

  /// Plural `/api/hosts/{id}`, deliberately — the singular `/api/host` is inside nginx's agent
  /// regex and would demand a client certificate this browser has not got.
  @override
  Future<void> requestRemoval(String id) => _api.delete('/api/hosts/${Uri.encodeComponent(id)}');

  /// A POST although it only reads: the server records every reveal, and a GET could be fired by
  /// another site navigating an administrator's browser, leaving reveals they never made.
  @override
  Future<AssignedPerson> assignedPerson(String id) async => assignedPersonFromJson(
        await _api.postJson('/api/hosts/${Uri.encodeComponent(id)}/assigned-person/reveal') as Map<String, dynamic>,
      );

  @override
  Future<void> clearAssignedPerson(String id) async {
    await _api.putJson('/api/hosts/profile', body: {'hostIds': [id], 'clearAssignedPerson': true});
  }

  @override
  Future<int> updateProfiles({
    required List<String> hostIds,
    String? organization,
    String? department,
    String? location,
  }) async {
    final answer = await _api.putJson('/api/hosts/profile', body: {
      'hostIds': hostIds,
      'organization': ?organization,
      'department': ?department,
      'location': ?location,
    }) as Map<String, dynamic>;
    return (answer['updated'] as num?)?.toInt() ?? 0;
  }

  @override
  Future<void> startMaintenance(String id, {DateTime? until, String? reason}) async {
    await _api.putJson('/api/hosts/${Uri.encodeComponent(id)}/maintenance', body: {
      'until': until?.toUtc().toIso8601String(),
      'reason': reason,
    });
  }

  @override
  Future<void> endMaintenance(String id) async {
    await _api.delete('/api/hosts/${Uri.encodeComponent(id)}/maintenance');
  }

  @override
  Future<HostProfileImportResult> importProfiles(List<HostProfileImportRow> rows) async =>
      importResultFromJson(
        await _api.postJson('/api/hosts/import', body: {'rows': [for (final row in rows) row.toJson()]})
            as Map<String, dynamic>,
      );
}
