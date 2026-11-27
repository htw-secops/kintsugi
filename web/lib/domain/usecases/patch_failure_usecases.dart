import '../entities/patch_failure.dart';
import '../entities/upgrade_path.dart';
import '../repositories/repositories.dart';

/// Every reported patch failure, fleet-wide — outstanding first.
class GetPatchFailures {
  const GetPatchFailures(this._repository);

  final PatchFailureRepository _repository;

  Future<List<PatchFailure>> call() => _repository.list();
}

/// Clears one failure off the screen by hand, for one that cannot recur.
class DismissPatchFailure {
  const DismissPatchFailure(this._repository);

  final PatchFailureRepository _repository;

  Future<void> call(String id) => _repository.dismiss(id);
}

/// "Re-verify now": re-checks outstanding failures against the last inventory each host sent.
class ReverifyPatchFailures {
  const ReverifyPatchFailures(this._repository);

  final PatchFailureRepository _repository;

  Future<ReverifyResult> call({String? hostId, String? applicationName}) =>
      _repository.reverify(hostId: hostId, applicationName: applicationName);
}

/// The repair Kintsugi drafted for a failing script, beside the script it would replace.
class GetPatchFailureRepairDraft {
  const GetPatchFailureRepairDraft(this._repository);

  final PatchFailureRepository _repository;

  Future<PatchFailureRepairDraft> call(String id) => _repository.repairDraft(id);
}

/// Signs a drafted repair, which makes it the script agents run and clears the path's failures.
class SignPatchFailureRepairDraft {
  const SignPatchFailureRepairDraft(this._repository);

  final PatchFailureRepository _repository;

  Future<UpgradePathResult> call({
    required String applicationName,
    required String platform,
    required String scriptSha256,
    required String patchFailureId,
  }) =>
      _repository.signRepairDraft(
        applicationName: applicationName,
        platform: platform,
        scriptSha256: scriptSha256,
        patchFailureId: patchFailureId,
      );
}
