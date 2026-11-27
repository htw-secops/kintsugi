import '../../core/network/api_client.dart';
import '../../core/network/json_reader.dart';
import '../../domain/entities/patch_failure.dart';
import '../../domain/entities/upgrade_path.dart';
import '../../domain/repositories/repositories.dart';
import '../models/patch_failure_mapper.dart';
import '../models/upgrade_path_mapper.dart';

/// Talks to `/api/admin/patch-failures`.
///
/// Under `/api/admin/` for the reason every browser-driven route is: `/api/patch-failures` — the
/// route the agents post to — is inside nginx's exact-match agent regex, so a browser calling it
/// gets a 403 for want of a client certificate.
class PatchFailureRepositoryImpl implements PatchFailureRepository {
  const PatchFailureRepositoryImpl(this._api);

  final ApiClient _api;

  static const _base = '/api/admin/patch-failures';

  @override
  Future<List<PatchFailure>> list() async =>
      listFromJson(await _api.getJson(_base), patchFailureFromJson);

  @override
  Future<ReverifyResult> reverify({String? hostId, String? applicationName}) async =>
      reverifyResultFromJson(
        (await _api.postJson('$_base/reverify', body: {
          'hostId': ?hostId,
          'applicationName': ?applicationName,
        })) as Map<String, dynamic>? ??
            const {},
      );

  @override
  Future<void> dismiss(String id) async {
    await _api.postJson('$_base/${Uri.encodeComponent(id)}/dismiss');
  }

  @override
  Future<PatchFailureRepairDraft> repairDraft(String id) async => patchFailureRepairDraftFromJson(
        await _api.getJson('$_base/${Uri.encodeComponent(id)}/repair-draft') as Map<String, dynamic>,
      );

  /// The same route the instructions panel signs through. It sits here because it is this screen's
  /// action, and each screen's repository interface holds only that screen's routes.
  @override
  Future<UpgradePathResult> signRepairDraft({
    required String applicationName,
    required String platform,
    required String scriptSha256,
    required String patchFailureId,
  }) async =>
      upgradePathResultFromJson(
        await _api.postJson('/api/upgrade-paths/sign-script', body: {
          'applicationName': applicationName,
          'platform': platform,
          'scriptSha256': scriptSha256,
          'patchFailureId': patchFailureId,
          'signCandidate': true,
        }) as Map<String, dynamic>,
      );
}
