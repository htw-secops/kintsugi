import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/host.dart';

/// Reads a `HostDto`.
HostSummary hostFromJson(Map<String, dynamic> json) => HostSummary(
      id: json['id'] as String,
      hostname: json['hostname'] as String? ?? '',
      serialNumber: json['serialNumber'] as String? ?? '',
      operatingSystem: json['operatingSystem'] as String?,
      ipAddress: json['ipAddress'] as String?,
      status: enumFromJson(
        json['status'],
        HostStatus.values,
        const ['Unknown', 'Online', 'Offline', 'Decommissioned'],
        HostStatus.unknown,
      ),
      lastSeenUtc: dateTimeFromJson(json['lastSeenUtc']),
      agentVersion: json['agentVersion'] as String?,
      operatingSystemUpdateAvailable: json['operatingSystemUpdateAvailable'] as bool?,
      operatingSystemLatestVersion: json['operatingSystemLatestVersion'] as String?,
      appUpdatesAvailableCount: (json['appUpdatesAvailableCount'] as num?)?.toInt() ?? 0,
      unpatchedCveCount: (json['unpatchedCveCount'] as num?)?.toInt() ?? 0,
      patchedCveCount: (json['patchedCveCount'] as num?)?.toInt() ?? 0,
      removalRequested: json['removalRequested'] as bool? ?? false,
      latestAgentVersion: json['latestAgentVersion'] as String?,
      agentUpdateState: enumFromJson(
        json['agentUpdateState'],
        AgentUpdateState.values,
        const ['Unknown', 'Current', 'Behind', 'NotUpdating'],
        AgentUpdateState.unknown,
      ),
      restartRequiredSinceUtc: dateTimeFromJson(json['restartRequiredSinceUtc']),
      restartRequiredBecause: json['restartRequiredBecause'] as String?,
      organization: _text(json['organization']),
      department: _text(json['department']),
      location: _text(json['location']),
      model: _text(json['model']),
      assignedShortName: _text(json['assignedShortName']),
      hasAssignedPerson: json['hasAssignedPerson'] as bool? ?? false,
      // An older server sends none of these, and a host with no word on its health is shown as
      // healthy rather than alarming: "unknown" would paint every row amber on a server that has
      // simply not been updated yet.
      health: enumFromJson(
        json['health'],
        HostHealthState.values,
        const ['Healthy', 'Pending', 'Problem'],
        HostHealthState.healthy,
      ),
      healthReason: _text(json['healthReason']),
      failedPatchCount: (json['failedPatchCount'] as num?)?.toInt() ?? 0,
      unstable: json['unstable'] as bool? ?? false,
      lastLoggedInUser: json['lastLoggedInUser'] as String?,
      inMaintenance: json['inMaintenance'] as bool? ?? false,
      maintenanceUntilUtc: dateTimeFromJson(json['maintenanceUntilUtc']),
      maintenanceReason: json['maintenanceReason'] as String?,
    );

/// A string field where "" means the same as null, so a cleared value and an absent one are one thing
/// on screen.
String? _text(Object? raw) => raw is String && raw.trim().isNotEmpty ? raw.trim() : null;

/// Reads the answer of `GET /api/hosts/{id}/assigned-person`.
AssignedPerson assignedPersonFromJson(Map<String, dynamic> json) =>
    AssignedPerson(name: _text(json['name']), email: _text(json['email']));

/// Reads the answer of `POST /api/hosts/import`.
HostProfileImportResult importResultFromJson(Map<String, dynamic> json) => HostProfileImportResult(
      matched: (json['matched'] as num?)?.toInt() ?? 0,
      notFound: (json['notFound'] as num?)?.toInt() ?? 0,
      updated: (json['updated'] as num?)?.toInt() ?? 0,
    );
