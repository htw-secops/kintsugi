/// Reads the Software screen's DTOs (`Kintsugi.Application/SoftwareTitles/`). Hand-mirrored — see
/// `.claude/rules/hand-mirrored-dtos.md`; nothing in CI cross-checks this against the C# records.
///
/// `SoftwareMode` and `SoftwareScriptState` cross as **names** (`JsonStringEnumConverter`), unlike
/// the ordinal enums elsewhere in this app. [enumFromJson] reads either form, but only the name is
/// trustworthy here: these enums have no ordinal contract, so an ordinal arriving would be a server
/// change worth noticing rather than one to decode.
library;

import '../../core/network/json_reader.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/software_title.dart';
import 'application_mapper.dart';

SoftwareMode? softwareModeFromJson(Object? raw) => raw is String
    ? enumFromJson<SoftwareMode?>(raw, SoftwareMode.values, [for (final m in SoftwareMode.values) m.wireName], null)
    : null;

SoftwareScriptState? softwareScriptStateFromJson(Object? raw) => raw is String
    ? enumFromJson<SoftwareScriptState?>(
        raw,
        SoftwareScriptState.values,
        [for (final s in SoftwareScriptState.values) s.wireName],
        null,
      )
    : null;

int _int(Object? raw) => (raw as num?)?.toInt() ?? 0;

String? _text(Object? raw) => raw is String && raw.isNotEmpty ? raw : null;

SoftwareTitle softwareTitleFromJson(Map<String, dynamic> json) => SoftwareTitle(
      id: json['id']?.toString() ?? '',
      name: json['name'] as String? ?? '',
      platform: json['platform'] as String? ?? '',
      packageManager: _text(json['packageManager']),
      packageId: _text(json['packageId']),
      detectAs: _text(json['detectAs']),
      mode: softwareModeFromJson(json['mode']),
      secretNames: stringListFromJson(json['secretNames']),
      assignedToAll: json['assignedToAll'] as bool? ?? false,
      assignedHostNames: stringListFromJson(json['assignedHostNames']),
      compliantCount: _int(json['compliantCount']),
      pendingCount: _int(json['pendingCount']),
      failedCount: _int(json['failedCount']),
      notApplicableCount: _int(json['notApplicableCount']),
      scriptState: softwareScriptStateFromJson(json['scriptState']),
    );

/// Reads a `ScriptSecretSummaryDto`. Reads nothing else: if a server ever sent a `value` here it
/// would be dropped on the floor rather than carried into the screen.
ScriptSecretSummary scriptSecretSummaryFromJson(Map<String, dynamic> json) => ScriptSecretSummary(
      name: json['name'] as String? ?? '',
      hasValue: json['hasValue'] as bool? ?? false,
      updatedBy: _text(json['updatedBy']),
      updatedUtc: dateTimeFromJson(json['updatedUtc']),
    );

SoftwareOverview softwareOverviewFromJson(Map<String, dynamic> json) => SoftwareOverview(
      titles: listFromJson(json['titles'], softwareTitleFromJson),
      secrets: listFromJson(json['secrets'], scriptSecretSummaryFromJson),
    );

/// Whether [raw] is the `{titles, secrets}` screen state rather than an empty or partial answer.
bool isSoftwareOverviewJson(Object? raw) => raw is Map<String, dynamic> && raw['titles'] is List;

/// Reads the run-now answer: `{requested, notRequested}`, and the screen state too when the server
/// sends it alongside.
SoftwareRunNowResult softwareRunNowResultFromJson(Map<String, dynamic> json) => SoftwareRunNowResult(
      result: forcedPatchRunResultFromJson(json),
      overview: isSoftwareOverviewJson(json) ? softwareOverviewFromJson(json) : null,
    );
