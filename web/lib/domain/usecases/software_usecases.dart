import '../entities/enums.dart';
import '../entities/software_title.dart';
import '../repositories/repositories.dart';

/// Every software title with its counts, and every script secret (names only).
class GetSoftware {
  const GetSoftware(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call() => _repository.read();
}

/// Adds a title: a package-manager id, or a custom title whose script is researched or written on
/// this screen.
class AddSoftwareTitle {
  const AddSoftwareTitle(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call(SoftwareTitleDraft draft) => _repository.create(draft);
}

/// Changes what a title wants and what it needs. Name, source and id are fixed once added.
class UpdateSoftwareTitle {
  const UpdateSoftwareTitle(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call(
    String id, {
    required SoftwareMode mode,
    String? detectAs,
    required List<String> secretNames,
  }) =>
      _repository.update(id, mode: mode, detectAs: detectAs, secretNames: secretNames);
}

/// Every host of the title's operating system, a list of host names, or both.
class AssignSoftwareTitle {
  const AssignSoftwareTitle(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call(String id, {required bool allHosts, required List<String> hostNames}) =>
      _repository.assign(id, allHosts: allHosts, hostNames: hostNames);
}

/// Asks every host still pending on a title to act within the minute rather than at its next
/// hourly check-in — a forced run, the same mechanism as the Applications screen's "Patch now".
class RunSoftwareTitleNow {
  const RunSoftwareTitleNow(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareRunNowResult> call(String id) => _repository.runNow(id);
}

/// Stops acting on a title. Removes nothing already installed.
class DeleteSoftwareTitle {
  const DeleteSoftwareTitle(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call(String id) => _repository.delete(id);
}

/// Sets or replaces a secret's value. Write-only: nothing that comes back carries the value.
class SetScriptSecret {
  const SetScriptSecret(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call(String name, String value) => _repository.setSecret(name, value);
}

/// Deletes a secret. The server refuses while a title still names it.
class DeleteScriptSecret {
  const DeleteScriptSecret(this._repository);

  final SoftwareRepository _repository;

  Future<SoftwareOverview> call(String name) => _repository.deleteSecret(name);
}
