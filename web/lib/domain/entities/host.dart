import 'package:equatable/equatable.dart';

import 'enums.dart';

/// How a host is doing, in three states, decided by the server. Mirrors the `health` string of
/// `HostDto`.
///
/// **Problem**: the host is unstable, or has a patch that failed. **Pending**: nothing is broken but
/// something is waiting: its agent is behind, or it has not been seen for days. **Healthy**: neither.
/// The same three colours (green, amber, red) are used everywhere the Hosts screen shows it.
enum HostHealthState {
  healthy('Healthy'),
  pending('Pending'),
  problem('Problem');

  const HostHealthState(this.label);

  final String label;

  /// Worst first, the order the Health column sorts in.
  int get severity => switch (this) {
        HostHealthState.problem => 0,
        HostHealthState.pending => 1,
        HostHealthState.healthy => 2,
      };
}

/// The person a host is assigned to, as the administrator sees them **after pressing Reveal**.
/// Mirrors the answer of `GET /api/hosts/{id}/assigned-person`. Either part can be null: the person
/// may have been imported without an email, or the name not at all.
class AssignedPerson extends Equatable {
  const AssignedPerson({this.name, this.email});

  final String? name;
  final String? email;

  @override
  List<Object?> get props => [name, email];
}

/// One row of an import: a host's serial number and only the columns the administrator chose to
/// send. A field that is null is not in the request at all.
class HostProfileImportRow extends Equatable {
  const HostProfileImportRow({
    required this.serialNumber,
    this.department,
    this.location,
    this.model,
    this.assignedName,
    this.assignedEmail,
  });

  final String serialNumber;
  final String? department;
  final String? location;
  final String? model;
  final String? assignedName;
  final String? assignedEmail;

  Map<String, dynamic> toJson() => {
        'serialNumber': serialNumber,
        if (department != null) 'department': department,
        if (location != null) 'location': location,
        if (model != null) 'model': model,
        if (assignedName != null) 'assignedName': assignedName,
        if (assignedEmail != null) 'assignedEmail': assignedEmail,
      };

  @override
  List<Object?> get props => [serialNumber, department, location, model, assignedName, assignedEmail];
}

/// What an import did: how many rows matched a host by serial number, how many did not, and how
/// many hosts were actually changed.
class HostProfileImportResult extends Equatable {
  const HostProfileImportResult({required this.matched, required this.notFound, required this.updated});

  final int matched;
  final int notFound;
  final int updated;

  @override
  List<Object?> get props => [matched, notFound, updated];
}

/// A host in the fleet, as the Hosts screen lists it. Mirrors `HostDto`.
class HostSummary extends Equatable {
  const HostSummary({
    required this.id,
    required this.hostname,
    required this.serialNumber,
    required this.operatingSystem,
    required this.ipAddress,
    required this.status,
    required this.lastSeenUtc,
    required this.agentVersion,
    required this.operatingSystemUpdateAvailable,
    required this.operatingSystemLatestVersion,
    required this.appUpdatesAvailableCount,
    required this.unpatchedCveCount,
    required this.patchedCveCount,
    required this.removalRequested,
    this.latestAgentVersion,
    this.agentUpdateState = AgentUpdateState.unknown,
    this.restartRequiredSinceUtc,
    this.restartRequiredBecause,
    this.organization,
    this.department,
    this.location,
    this.model,
    this.assignedShortName,
    this.hasAssignedPerson = false,
    this.health = HostHealthState.healthy,
    this.healthReason,
    this.failedPatchCount = 0,
    this.unstable = false,
    this.lastLoggedInUser,
  });

  final String id;
  final String hostname;

  /// The host's own identity — it becomes the certificate CN every authenticated request is
  /// checked against. On Windows and Linux it is frequently a placeholder the agent refuses to
  /// enroll with, which is why it is worth showing rather than hiding as an internal key.
  final String serialNumber;

  final String? operatingSystem;
  final String? ipAddress;
  final HostStatus status;
  final DateTime? lastSeenUtc;

  /// The version the host's agent reported on its last check-in, mirroring
  /// `HostDto.AgentVersion`. Null when the agent predates the field and has never reported one —
  /// the server keeps whatever it last heard rather than clearing it, so null means "never", not
  /// "not this time".
  final String? agentVersion;

  /// Tri-state on purpose: null is "not checked", which is a different thing from "up to date"
  /// and has to read differently on screen.
  final bool? operatingSystemUpdateAvailable;

  final String? operatingSystemLatestVersion;
  final int appUpdatesAvailableCount;

  /// CVEs on this host not confirmed fixed: applications with an update available or never
  /// researched, and the operating system — and every distribution package it shipped, which has
  /// no upgrade path of its own but is patched by the same OS update — when an OS update is
  /// pending or has never been checked. Anything short of a confirmed-current verdict lands here
  /// rather than being dropped as a coverage gap.
  final int unpatchedCveCount;

  /// CVEs matched against an application, or against the OS and its packages when no OS update is
  /// pending, confirmed already current — a CVE the latest known version does not fix, rather than
  /// one patching would clear.
  final int patchedCveCount;

  /// True once removal has been requested and the agent has not yet confirmed it uninstalled
  /// itself.
  final bool removalRequested;

  /// The newest agent published for this host's own platform (Windows, macOS and Linux agents have
  /// separate version numbers), or null when there is none to compare with.
  final String? latestAgentVersion;

  /// Whether [agentVersion] is behind that, and whether it looks stuck. Decided by the server.
  final AgentUpdateState agentUpdateState;
  /// When the host first said it needs a restart for updates it has already installed, or null when
  /// it does not (or cannot say: only the Linux agent reports it).
  final DateTime? restartRequiredSinceUtc;

  /// What asked for it, as the host names it.
  final String? restartRequiredBecause;

  /// Who was logged on at the host's last check-in that found anybody, comma-separated, mirroring
  /// `HostDto.LastLoggedInUser`. Null until an agent that reports it has found somebody; the server
  /// keeps it while nobody is logged on, so a laptop at its login window is still found by its owner.
  final String? lastLoggedInUser;

  bool get restartRequired => restartRequiredSinceUtc != null;

  /// Where the host belongs, as an administrator or an import set them. All null until somebody does:
  /// the agent knows none of it.
  final String? organization;
  final String? department;
  final String? location;

  /// The hardware model. Comes from an import for now; the agents do not report it yet.
  final String? model;

  /// First name and the initial of the surname ("Alex M."), made by the **server**. The full name and
  /// the email never come in the list: they are fetched, and audited, only when an administrator
  /// presses Reveal.
  final String? assignedShortName;

  /// Whether a name or email is stored at all, so the panel can say "not set yet" without asking.
  final bool hasAssignedPerson;

  /// The three-state health the server worked out, and the sentence behind it ("Unstable, 2 failed
  /// patches", "Agent behind", "Not seen for 3 days"). Healthy has no reason.
  final HostHealthState health;
  final String? healthReason;
  final int failedPatchCount;
  final bool unstable;

  /// True while the host has checked in within the last interval. The Hosts screen calls it
  /// "Connected"; here it is only that — agents check in hourly, they do not hold a socket open.
  bool get isConnected => status == HostStatus.online;

  @override
  List<Object?> get props => [
        id,
        hostname,
        serialNumber,
        operatingSystem,
        ipAddress,
        status,
        lastSeenUtc,
        agentVersion,
        operatingSystemUpdateAvailable,
        operatingSystemLatestVersion,
        appUpdatesAvailableCount,
        unpatchedCveCount,
        patchedCveCount,
        removalRequested,
        latestAgentVersion,
        agentUpdateState,
        restartRequiredSinceUtc,
        restartRequiredBecause,
        organization,
        department,
        location,
        model,
        assignedShortName,
        hasAssignedPerson,
        health,
        healthReason,
        failedPatchCount,
        unstable,
        lastLoggedInUser,
      ];
}
