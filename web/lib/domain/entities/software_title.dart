import 'package:equatable/equatable.dart';

import 'application.dart';
import 'enums.dart';

/// The package managers a software title can name, with the operating system each one runs on.
///
/// Mirrors the managers `RecognizedPackageManager.SupportsActions` is true for, spelled the way
/// `PackageManagerCatalog` spells them (that casing is what the server canonicalises to). App Store
/// is deliberately absent: `mas install` needs the numeric track id and Kintsugi only holds the
/// bundle id — see "Software titles" in `src/CLAUDE.md`.
const softwarePackageManagers = <String, String>{
  'winget': 'Windows',
  'Chocolatey': 'Windows',
  'Homebrew': 'macOS',
  'Flatpak': 'Linux',
  'Snap': 'Linux',
};

/// The operating-system buckets a custom title can be written for (`PlatformBucket`'s constants).
const softwareOperatingSystems = ['Windows', 'macOS', 'Linux'];

/// A secret name the server will accept: `ScriptSecret`'s own rule, checked here only so the
/// screen can say so before the round trip. It is also the suffix of the `KINTSUGI_SECRET_<NAME>`
/// environment variable a script reads, which is why it is upper-case and shell-safe.
final softwareSecretNamePattern = RegExp(r'^[A-Z][A-Z0-9_]{0,63}$');

/// One row of the Software screen. Mirrors `SoftwareTitleDto`.
class SoftwareTitle extends Equatable {
  const SoftwareTitle({
    required this.id,
    required this.name,
    required this.platform,
    this.packageManager,
    this.packageId,
    this.detectAs,
    required this.mode,
    this.secretNames = const [],
    this.assignedToAll = false,
    this.assignedHostNames = const [],
    this.compliantCount = 0,
    this.pendingCount = 0,
    this.failedCount = 0,
    this.notApplicableCount = 0,
    required this.scriptState,
  });

  final String id;
  final String name;

  /// The title's platform bucket as the server stores it: an operating system (`Windows`, `macOS`,
  /// `Linux`) for a custom title, or a manager's bucket (`pm:winget`) for a package-manager one.
  final String platform;

  /// Null for a custom title, whose script is an upgrade path of its own.
  final String? packageManager;
  final String? packageId;
  final String? detectAs;

  /// Null only when the server sent a mode this client does not know — shown as such rather than
  /// guessed, because guessing "Required" for an unknown mode and then saving the form would turn
  /// it into one.
  final SoftwareMode? mode;
  final List<String> secretNames;
  final bool assignedToAll;
  final List<String> assignedHostNames;
  final int compliantCount;
  final int pendingCount;
  final int failedCount;
  final int notApplicableCount;

  /// Null for a state this client does not know.
  final SoftwareScriptState? scriptState;

  bool get isCustom => packageManager == null;

  /// What the Source column says.
  String get sourceLabel => packageManager ?? 'Custom';

  /// The operating system the title's hosts run, whichever kind of bucket [platform] is.
  String get operatingSystem {
    final manager = packageManager ?? (platform.startsWith('pm:') ? platform.substring(3) : null);
    if (manager != null) {
      for (final entry in softwarePackageManagers.entries) {
        if (entry.key.toLowerCase() == manager.toLowerCase()) return entry.value;
      }
    }
    return platform;
  }

  /// The manager's own platform bucket, which is where its one shared script lives on Upgrade
  /// Scripts. Null for a custom title.
  String? get managerBucket => packageManager == null ? null : 'pm:$packageManager';

  /// Whether this title is assigned to any host at all.
  bool get isAssigned => assignedToAll || assignedHostNames.isNotEmpty;

  @override
  List<Object?> get props => [
        id,
        name,
        platform,
        packageManager,
        packageId,
        detectAs,
        mode,
        secretNames,
        assignedToAll,
        assignedHostNames,
        compliantCount,
        pendingCount,
        failedCount,
        notApplicableCount,
        scriptState,
      ];
}

/// A script secret as the admin UI may see it: whether it has a value and who last set it, never
/// the value. Mirrors `ScriptSecretSummaryDto`; there is no field on the wire that could carry the
/// value, and this class deliberately has none either.
class ScriptSecretSummary extends Equatable {
  const ScriptSecretSummary({
    required this.name,
    required this.hasValue,
    this.updatedBy,
    this.updatedUtc,
  });

  final String name;
  final bool hasValue;
  final String? updatedBy;
  final DateTime? updatedUtc;

  @override
  List<Object?> get props => [name, hasValue, updatedBy, updatedUtc];
}

/// The whole Software screen: every admin call answers with this, so the screen never has to work
/// out what a change did to the counts.
class SoftwareOverview extends Equatable {
  const SoftwareOverview({this.titles = const [], this.secrets = const []});

  final List<SoftwareTitle> titles;
  final List<ScriptSecretSummary> secrets;

  @override
  List<Object?> get props => [titles, secrets];
}

/// What "Run now" did: the forced runs requested, plus the screen re-read when the server sent it.
class SoftwareRunNowResult extends Equatable {
  const SoftwareRunNowResult({required this.result, this.overview});

  final ForcedPatchRunResult result;

  /// Null when the run-now answer carried only the counts; the caller re-reads the screen then.
  final SoftwareOverview? overview;

  @override
  List<Object?> get props => [result, overview];
}

/// What the Add Software dialog produces. Mirrors the `POST /api/admin/software` body.
class SoftwareTitleDraft extends Equatable {
  const SoftwareTitleDraft({
    required this.name,
    this.packageManager,
    this.platform,
    this.packageId,
    this.detectAs,
    required this.mode,
    this.secretNames = const [],
  });

  final String name;

  /// Null for a custom title.
  final String? packageManager;

  /// The operating system, for a custom title only; a manager's title takes the manager's.
  final String? platform;
  final String? packageId;
  final String? detectAs;
  final SoftwareMode mode;
  final List<String> secretNames;

  Map<String, dynamic> toJson() => {
        'name': name,
        'packageManager': packageManager,
        'platform': packageManager == null ? platform : null,
        'packageId': packageId,
        'detectAs': detectAs,
        'mode': mode.wireName,
        'secretNames': secretNames,
      };

  @override
  List<Object?> get props => [name, packageManager, platform, packageId, detectAs, mode, secretNames];
}
