/// Reads the Baselines screen's DTOs (`Kintsugi.Application/Baselines/BaselineDtos.cs`). Hand-mirrored
/// — see `.claude/rules/hand-mirrored-dtos.md`; nothing in CI cross-checks this against the C# records.
library;

import '../../core/network/json_reader.dart';
import '../../domain/entities/baseline.dart';
import '../../domain/entities/enums.dart';

const _stateNames = ['Compliant', 'Drifted', 'NotApplicable', 'Error'];

/// `BaselineItemState` arrives as an ordinal (it has no converter); a name is read too, and anything
/// unrecognised is null rather than a guess.
BaselineItemState? baselineItemStateFromJson(Object? raw) =>
    raw == null ? null : enumFromJson<BaselineItemState?>(raw, BaselineItemState.values, _stateNames, null);

int _int(Object? raw) => (raw as num?)?.toInt() ?? 0;

ComplianceCounts complianceCountsFromJson(Object? raw) {
  final json = raw is Map<String, dynamic> ? raw : const <String, dynamic>{};
  return ComplianceCounts(
    compliant: _int(json['compliant']),
    drifted: _int(json['drifted']),
    notApplicable: _int(json['notApplicable']),
    error: _int(json['error']),
    notReported: _int(json['notReported']),
  );
}

BaselineSummary baselineSummaryFromJson(Map<String, dynamic> json) => BaselineSummary(
      id: json['id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      latestVersion: _int(json['latestVersion']),
      signedVersion: (json['signedVersion'] as num?)?.toInt(),
      assignedToAllWindowsHosts: json['assignedToAllWindowsHosts'] as bool? ?? false,
      applicableHostCount: _int(json['applicableHostCount']),
      settingCount: _int(json['settingCount']),
      totals: complianceCountsFromJson(json['totals']),
      assignedApplicationName: _nonBlank(json['assignedApplicationName']),
    );

String? _nonBlank(Object? raw) => raw is String && raw.trim().isNotEmpty ? raw : null;

BaselineItem baselineItemFromJson(Map<String, dynamic> json) => BaselineItem(
      key: json['key'] as String? ?? '',
      kind: json['kind'] as String? ?? '',
      scope: json['scope'] as String? ?? '',
      hive: json['hive'] as String? ?? '',
      path: json['path'] as String? ?? '',
      valueName: json['valueName'] as String? ?? '',
      valueType: json['valueType'] as String?,
      data: json['data'] as String?,
      secret: json['secret'] as String?,
      mode: json['mode'] as String?,
      reason: json['reason'] as String?,
      signatureName: json['signatureName'] as String?,
    );

BaselineItem _itemOf(Object? raw) =>
    baselineItemFromJson(raw is Map<String, dynamic> ? raw : const <String, dynamic>{});

BaselineVersion baselineVersionFromJson(Map<String, dynamic> json) => BaselineVersion(
      id: json['id'] as String? ?? '',
      number: _int(json['number']),
      document: json['document'] as String? ?? '',
      documentSha256: json['documentSha256'] as String? ?? '',
      signed: json['signed'] as bool? ?? false,
      signedBy: json['signedBy'] as String?,
      signedUtc: dateTimeFromJson(json['signedUtc']),
      createdBy: json['createdBy'] as String?,
      createdUtc: dateTimeRequiredFromJson(json['createdUtc']),
    );

BaselineDetail baselineDetailFromJson(Map<String, dynamic> json) => BaselineDetail(
      id: json['id'] as String? ?? '',
      name: json['name'] as String? ?? '',
      assignedToAllWindowsHosts: json['assignedToAllWindowsHosts'] as bool? ?? false,
      assignedHostNames: stringListFromJson(json['assignedHostNames']),
      versions: listFromJson(json['versions'], baselineVersionFromJson),
      items: listFromJson(
        json['items'],
        (item) => BaselineItemCompliance(item: _itemOf(item['item']), counts: complianceCountsFromJson(item['counts'])),
      ),
      hosts: listFromJson(
        json['hosts'],
        (host) => BaselineHostCompliance(
          hostId: host['hostId'] as String? ?? '',
          hostname: host['hostname'] as String? ?? '',
          counts: complianceCountsFromJson(host['counts']),
          lastCheckedUtc: dateTimeFromJson(host['lastCheckedUtc']),
        ),
      ),
      assignedApplicationName: _nonBlank(json['assignedApplicationName']),
      assignedApplicationHostCount: _int(json['assignedApplicationHostCount']),
    );

BaselineHostDetail baselineHostDetailFromJson(Map<String, dynamic> json) => BaselineHostDetail(
      hostId: json['hostId'] as String? ?? '',
      hostname: json['hostname'] as String? ?? '',
      signedVersion: (json['signedVersion'] as num?)?.toInt(),
      items: listFromJson(
        json['items'],
        (row) => HostItemState(
          item: _itemOf(row['item']),
          state: baselineItemStateFromJson(row['state']),
          observedValue: row['observedValue'] as String?,
          detail: row['detail'] as String?,
          lastCheckedUtc: dateTimeFromJson(row['lastCheckedUtc']),
          lastChangedUtc: dateTimeFromJson(row['lastChangedUtc']),
        ),
      ),
      facts: listFromJson(
        json['facts'],
        (fact) => HostFact(
          key: fact['key'] as String? ?? '',
          value: fact['value'] as String?,
          lastCheckedUtc: dateTimeFromJson(fact['lastCheckedUtc']),
          lastChangedUtc: dateTimeFromJson(fact['lastChangedUtc']),
        ),
      ),
    );

/// `ResolvedBy` is a plain string on the wire (`"gcpw"`, `"username"`, or null). Anything else reads as
/// unresolved rather than as a guess at which match it was.
PersonResolution personResolutionFromJson(Object? raw) => switch (raw is String ? raw.toLowerCase() : null) {
      'gcpw' => PersonResolution.gcpw,
      'username' => PersonResolution.username,
      _ => PersonResolution.unresolved,
    };

/// Reads one `BaselinePersonRowDto` (`Kintsugi.Application/Baselines/Queries/GetBaselinePeople/`), from
/// `GET /api/admin/baselines/{id}/people`. Its `state` is an ordinal, like `HostItemStateDto`'s.
BaselinePersonRow baselinePersonRowFromJson(Map<String, dynamic> json) => BaselinePersonRow(
      hostId: json['hostId'] as String? ?? '',
      hostname: json['hostname'] as String? ?? '',
      windowsUser: json['windowsUser'] as String? ?? '',
      email: json['email'] as String?,
      resolvedBy: personResolutionFromJson(json['resolvedBy']),
      reason: json['reason'] as String?,
      copyRenderedAtUtc: dateTimeFromJson(json['copyRenderedAtUtc']),
      itemKey: json['itemKey'] as String? ?? '',
      state: baselineItemStateFromJson(json['state']),
      observed: json['observed'] as String?,
      detail: json['detail'] as String?,
      lastCheckedUtc: dateTimeFromJson(json['lastCheckedUtc']),
      lastSeenUtc: dateTimeFromJson(json['lastSeenUtc']),
    );
