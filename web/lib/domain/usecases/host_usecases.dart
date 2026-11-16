import '../entities/host.dart';
import '../repositories/repositories.dart';

class GetHosts {
  const GetHosts(this._repository);

  final HostRepository _repository;

  Future<List<HostSummary>> call() => _repository.list();
}

/// Asks a host's agent to uninstall itself on its next check-in.
class RequestHostRemoval {
  const RequestHostRemoval(this._repository);

  final HostRepository _repository;

  Future<void> call(String id) => _repository.requestRemoval(id);
}

/// Fetches the full name and email of the person a host is assigned to.
///
/// The server records every call (an "assigned person revealed" line on the host's timeline with the
/// administrator's name), so it is called once, from the Reveal button, and never to pre-load.
class RevealAssignedPerson {
  const RevealAssignedPerson(this._repository);

  final HostRepository _repository;

  Future<AssignedPerson> call(String hostId) => _repository.assignedPerson(hostId);
}

/// Deletes the stored name and email of the person a host is assigned to (recorded on its timeline).
class ClearAssignedPerson {
  const ClearAssignedPerson(this._repository);

  final HostRepository _repository;

  Future<void> call(String hostId) => _repository.clearAssignedPerson(hostId);
}

/// Sets department, location or organization on the selected hosts.
class UpdateHostProfiles {
  const UpdateHostProfiles(this._repository);

  final HostRepository _repository;

  /// A null argument keeps what the host has; an empty string clears it. Returns hosts changed.
  Future<int> call({
    required List<String> hostIds,
    String? organization,
    String? department,
    String? location,
  }) =>
      _repository.updateProfiles(
        hostIds: hostIds,
        organization: organization,
        department: department,
        location: location,
      );
}

/// Applies rows read from a Snipe-IT export, by serial number.
class ImportHostProfiles {
  const ImportHostProfiles(this._repository);

  final HostRepository _repository;

  Future<HostProfileImportResult> call(List<HostProfileImportRow> rows) => _repository.importProfiles(rows);
}
