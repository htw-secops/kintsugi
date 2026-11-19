import 'package:equatable/equatable.dart';

import 'enums.dart';

/// How many hosts are in each state for one item or across a baseline. [notReported] is a host the
/// baseline applies to that has sent nothing for it yet.
class ComplianceCounts extends Equatable {
  const ComplianceCounts({
    this.compliant = 0,
    this.drifted = 0,
    this.notApplicable = 0,
    this.error = 0,
    this.notReported = 0,
  });

  final int compliant;
  final int drifted;
  final int notApplicable;
  final int error;
  final int notReported;

  @override
  List<Object?> get props => [compliant, drifted, notApplicable, error, notReported];
}

/// A row of the Baselines screen.
class BaselineSummary extends Equatable {
  const BaselineSummary({
    required this.id,
    required this.name,
    required this.latestVersion,
    required this.signedVersion,
    required this.assignedToAllWindowsHosts,
    required this.applicableHostCount,
    required this.settingCount,
    required this.totals,
    this.assignedApplicationName,
  });

  final String id;
  final String name;
  final int latestVersion;

  /// The version agents are given, or null while nothing is signed.
  final int? signedVersion;
  final bool assignedToAllWindowsHosts;
  final int applicableHostCount;

  /// The signed version's judged items: settings and per-person items alike.
  final int settingCount;
  final ComplianceCounts totals;

  /// The "Windows hosts with this application installed" target, or null.
  final String? assignedApplicationName;

  @override
  List<Object?> get props => [
        id,
        name,
        latestVersion,
        signedVersion,
        assignedToAllWindowsHosts,
        applicableHostCount,
        settingCount,
        totals,
        assignedApplicationName,
      ];
}

/// The item kinds the server's `BaselineItemRules` accepts.
abstract final class BaselineItemKinds {
  static const setting = 'setting';
  static const fact = 'fact';

  /// One Outlook signature per signed-in person (IP-871), judged once per *person* rather than per host.
  static const outlookSignature = 'outlook-signature';
}

/// One item of a signed baseline document, exactly as the server's `BaselineItem` writes it.
class BaselineItem extends Equatable {
  const BaselineItem({
    required this.key,
    required this.kind,
    required this.scope,
    required this.hive,
    required this.path,
    required this.valueName,
    this.valueType,
    this.data,
    this.secret,
    this.mode,
    this.reason,
    this.signatureName,
  });

  final String key;

  /// `setting`, `fact` or `outlook-signature`: see [BaselineItemKinds].
  final String kind;

  /// `machine` or `user`.
  final String scope;
  final String hive;
  final String path;
  final String valueName;
  final String? valueType;
  final String? data;

  /// The name of the secret that supplies the value; the value itself never reaches this client.
  final String? secret;

  /// `audit` or `enforce`. Null reads as audit, which is what the agent does with it.
  final String? mode;
  final String? reason;

  /// The signature's Windows file name: an `outlook-signature` item's only, null for every other kind.
  final String? signatureName;

  bool get isSetting => kind == BaselineItemKinds.setting;

  bool get isOutlookSignature => kind == BaselineItemKinds.outlookSignature;

  /// Judged once per signed-in person rather than once per host. Decided by kind, not by [scope], as
  /// the server's `BaselineItem.IsPerPerson` is: a user-scope *setting* is still judged per host.
  bool get isPerPerson => isOutlookSignature;

  /// Has a state to count (the server's `IsJudged`). Facts are only reported.
  bool get isJudged => isSetting || isPerPerson;

  /// [mode] as shown, with the default spelt out.
  String get modeLabel => mode ?? 'audit';

  /// What the item should read, as the screen shows it.
  String get expected => isOutlookSignature
      ? '$modeLabel · per user'
      : secret != null
          ? 'secret: $secret'
          : '${data ?? ''} (${valueType ?? '?'})';

  /// What the item checks: `HKLM\path\valueName`, the way a reader would type it into regedit; or, for an
  /// `outlook-signature` item, the signature's name, since the values it writes are the agent's fixed list
  /// (`BaselineItemRules.SignatureWrites`).
  String get location => isOutlookSignature
      ? 'Outlook signature "${signatureName ?? ''}"'
      : '$hive\\$path${valueName.isEmpty ? '' : '\\$valueName'}';

  @override
  List<Object?> get props =>
      [key, kind, scope, hive, path, valueName, valueType, data, secret, mode, reason, signatureName];
}

class BaselineVersion extends Equatable {
  const BaselineVersion({
    required this.id,
    required this.number,
    required this.document,
    required this.documentSha256,
    required this.signed,
    this.signedBy,
    this.signedUtc,
    this.createdBy,
    required this.createdUtc,
  });

  final String id;
  final int number;

  /// The canonical JSON the signature covers, byte for byte. Signing hashes *this* string.
  final String document;
  final String documentSha256;
  final bool signed;
  final String? signedBy;
  final DateTime? signedUtc;
  final String? createdBy;
  final DateTime createdUtc;

  @override
  List<Object?> get props => [id, number, document, documentSha256, signed, signedBy, signedUtc, createdBy, createdUtc];
}

class BaselineItemCompliance extends Equatable {
  const BaselineItemCompliance({required this.item, required this.counts});

  final BaselineItem item;
  final ComplianceCounts counts;

  @override
  List<Object?> get props => [item, counts];
}

class BaselineHostCompliance extends Equatable {
  const BaselineHostCompliance({
    required this.hostId,
    required this.hostname,
    required this.counts,
    this.lastCheckedUtc,
  });

  final String hostId;
  final String hostname;
  final ComplianceCounts counts;
  final DateTime? lastCheckedUtc;

  @override
  List<Object?> get props => [hostId, hostname, counts, lastCheckedUtc];
}

class BaselineDetail extends Equatable {
  const BaselineDetail({
    required this.id,
    required this.name,
    required this.assignedToAllWindowsHosts,
    required this.assignedHostNames,
    required this.versions,
    required this.items,
    required this.hosts,
    this.assignedApplicationName,
    this.assignedApplicationHostCount = 0,
  });

  final String id;
  final String name;
  final bool assignedToAllWindowsHosts;
  final List<String> assignedHostNames;

  /// Newest first.
  final List<BaselineVersion> versions;

  /// The signed version's items, empty until one is signed.
  final List<BaselineItemCompliance> items;
  final List<BaselineHostCompliance> hosts;

  /// The "Windows hosts with this application installed" target, or null.
  final String? assignedApplicationName;

  /// How many Windows hosts [assignedApplicationName] reaches on its own; [hosts] is the whole assignment.
  final int assignedApplicationHostCount;

  /// The version agents run: the newest signed one.
  BaselineVersion? get signedVersion => versions.where((v) => v.signed).firstOrNull;

  /// Whether the signed version holds an item judged per person, which is what offers the People view.
  bool get hasPerPersonItems => items.any((i) => i.item.isPerPerson);

  @override
  List<Object?> get props => [
        id,
        name,
        assignedToAllWindowsHosts,
        assignedHostNames,
        versions,
        items,
        hosts,
        assignedApplicationName,
        assignedApplicationHostCount,
      ];
}

class HostItemState extends Equatable {
  const HostItemState({
    required this.item,
    this.state,
    this.observedValue,
    this.detail,
    this.lastCheckedUtc,
    this.lastChangedUtc,
  });

  final BaselineItem item;

  /// Null until the host has reported this item. For a per-person item, the worst of the host's people.
  final BaselineItemState? state;
  final String? observedValue;
  final String? detail;
  final DateTime? lastCheckedUtc;
  final DateTime? lastChangedUtc;

  @override
  List<Object?> get props => [item, state, observedValue, detail, lastCheckedUtc, lastChangedUtc];
}

class HostFact extends Equatable {
  const HostFact({required this.key, this.value, this.lastCheckedUtc, this.lastChangedUtc});

  final String key;
  final String? value;
  final DateTime? lastCheckedUtc;
  final DateTime? lastChangedUtc;

  @override
  List<Object?> get props => [key, value, lastCheckedUtc, lastChangedUtc];
}

class BaselineHostDetail extends Equatable {
  const BaselineHostDetail({
    required this.hostId,
    required this.hostname,
    this.signedVersion,
    required this.items,
    required this.facts,
  });

  final String hostId;
  final String hostname;
  final int? signedVersion;
  final List<HostItemState> items;
  final List<HostFact> facts;

  bool get hasPerPersonItems => items.any((i) => i.item.isPerPerson);

  @override
  List<Object?> get props => [hostId, hostname, signedVersion, items, facts];
}

/// How the server matched a signed-in Windows session to a person (`HostPersonResolution.ResolvedBy`).
enum PersonResolution {
  /// The Google Credential Provider for Windows named the person's email.
  gcpw,

  /// The Windows username matched exactly one email's local part plus the configured suffix.
  username,

  /// Nobody: no match, or more than one. The row's reason says which.
  unresolved;

  String get label => switch (this) {
        PersonResolution.gcpw => 'GCPW',
        PersonResolution.username => 'Username',
        PersonResolution.unresolved => 'Unresolved',
      };
}

/// One row of a baseline's People view: one signed-in Windows session on one host, against one
/// per-person item of the served version. [state] and what follows are null until that person has
/// reported, and always for a session nobody was resolved to.
class BaselinePersonRow extends Equatable {
  const BaselinePersonRow({
    required this.hostId,
    required this.hostname,
    required this.windowsUser,
    this.email,
    required this.resolvedBy,
    this.reason,
    this.copyRenderedAtUtc,
    required this.itemKey,
    this.state,
    this.observed,
    this.detail,
    this.lastCheckedUtc,
    this.lastSeenUtc,
  });

  final String hostId;
  final String hostname;
  final String windowsUser;
  final String? email;
  final PersonResolution resolvedBy;

  /// Why the session resolved as it did, chiefly why it did not.
  final String? reason;

  /// When the source rendered this person's signature copy; null when the server holds none.
  final DateTime? copyRenderedAtUtc;
  final String itemKey;
  final BaselineItemState? state;
  final String? observed;
  final String? detail;
  final DateTime? lastCheckedUtc;

  /// When the agent last listed this session. A person's last row keeps counting for the host for up to
  /// 30 days after they sign out (IP-871), and this is how a reader tells such a row from a current one.
  final DateTime? lastSeenUtc;

  @override
  List<Object?> get props => [
        hostId,
        hostname,
        windowsUser,
        email,
        resolvedBy,
        reason,
        copyRenderedAtUtc,
        itemKey,
        state,
        observed,
        detail,
        lastCheckedUtc,
        lastSeenUtc,
      ];
}
