import 'package:intl/intl.dart';

import '../../domain/entities/autonomy.dart';

/// "2026-11" as "November 2026".
String monthLabel(String month) {
  final date = DateTime.tryParse('$month-01');
  return date == null ? month : DateFormat('MMMM yyyy').format(date);
}

/// "2026-11" as "Nov".
String monthShort(String month) {
  final date = DateTime.tryParse('$month-01');
  return date == null ? month : DateFormat('MMM').format(date);
}

/// The month the page opens on: the current one.
String currentMonth(DateTime now) => DateFormat('yyyy-MM').format(now.toUtc());

/// What an event says, in words, for the feed.
String eventSummary(AutonomyEvent e) => switch (e.kind) {
      'ApplicationPatched' || 'OperatingSystemPatched' => 'Patched ${e.subject}',
      'PatchFailed' => e.count > 1 ? 'Failed to install ${e.subject} (${e.count} tries)' : 'Failed to install ${e.subject}',
      'PatchNowRequested' => 'Patch now requested for ${e.subject}',
      'PatchNowPickedUp' => 'The host picked up the Patch now request for ${e.subject}',
      'FailureDismissed' => 'Dismissed the failure for ${e.subject}',
      _ => e.kind,
    };

/// How a cause slows patching down. By kind, because the cause's own explanation says why it happens.
String howItSlows(AutonomyCause cause) => switch (cause.category) {
      'Disconnected' => 'Patching waits for the device to come back. A Patch now request is kept for 24 hours '
          'and starts the moment it checks in.',
      'Restart' => 'The patch is not in effect until the device restarts, so it still counts as exposed even '
          'though the install succeeded.',
      'Maintenance' => 'Not measured: Kintsugi has no maintenance mode yet.',
      _ => 'It fails on every cycle, so each retry costs time and the update does not land until the cause is '
          'cleared.',
    };

/// The fix to show: the server's own for the cause, or a fixed sentence for the kinds that have none.
String fixFor(AutonomyCause cause) =>
    cause.suggestedFix ??
    switch (cause.category) {
      'Disconnected' => 'Nothing to repair. Ask owners of devices offline for a long time to reconnect.',
      'Restart' => 'Ask the person to restart. A shorter delay budget in the Patching Policy forces a postponed restart sooner.',
      _ => 'Open Failed Updates and read the error.',
    };

/// How one KPI's bands read: "≤2%", "≥98%", or "—" for a figure with none.
({String green, String amber, String red}) bandsOf(AutonomyKpi k) {
  String n(double v) => '${v == v.roundToDouble() ? v.toInt() : v}${k.isPercent ? '%' : ''}';
  return switch (k.direction) {
    'LowerIsBetter' => (green: '≤${n(k.green)}', amber: '≤${n(k.amber)}', red: '>${n(k.amber)}'),
    'HigherIsBetter' => (green: '≥${n(k.green)}', amber: '≥${n(k.amber)}', red: '<${n(k.amber)}'),
    _ => (green: '—', amber: '—', red: '—'),
  };
}

String kpiValue(AutonomyKpi k, [double? value]) {
  final v = value ?? k.value;
  return '${v == v.roundToDouble() ? v.toInt() : v}${k.isPercent ? '%' : ''}';
}

/// "▲1.2 pp" / "▼0.3 pp", or "—" with no prior.
String kpiChange(AutonomyKpi k) {
  final d = k.delta;
  if (d == null) return '—';
  if (d == 0) return '0';
  return '${d > 0 ? '▲' : '▼'}${d.abs()}${k.isPercent ? ' pp' : ''}';
}

String statusWord(String status) => switch (status) {
      'R' => 'RED',
      'A' => 'AMBER',
      'G' => 'GREEN',
      _ => 'INFO',
    };

/// One thing to do to patch faster, in order of the delay it would recover.
class SpeedAction {
  const SpeedAction({required this.title, required this.devices, required this.deviceDays, required this.sharePercent, required this.effort});

  final String title;
  final int devices;
  final double deviceDays;
  final int sharePercent;
  final String effort;
}

/// The actions that follow from the measured causes, most delay recovered first. Only the causes' own
/// fixes: nothing here is an estimate of how much faster patching would get.
List<SpeedAction> speedActions(Autonomy data) {
  final actions = <SpeedAction>[];

  for (final cause in data.causes.where((c) => c.measured && c.devices > 0)) {
    final String title;
    final String effort;
    switch (cause.category) {
      case 'Failure' when cause.canPatchNow:
        title = 'Clear "${cause.title}" with Patch now';
        effort = 'One action, all ${cause.patchNowConnected + cause.patchNowOffline} together';
      case 'Failure':
        title = 'Fix "${cause.title}": ${fixFor(cause)}';
        effort = 'Once per cause';
      case 'Restart':
        title = 'Shorten the restart delay budget so a postponed restart is forced sooner';
        effort = 'A policy change in Patching Policy';
      case 'Disconnected':
        title = 'Ask the owners of devices offline for a long time to reconnect';
        effort = 'Cannot be forced; follow up by hand';
      default:
        continue;
    }
    actions.add(SpeedAction(
      title: title,
      devices: cause.devices,
      deviceDays: cause.deviceDays,
      sharePercent: cause.sharePercent,
      effort: effort,
    ));
  }

  actions.sort((a, b) => b.deviceDays.compareTo(a.deviceDays));
  return actions;
}

/// What each step of the autonomy ladder honestly is today. Moves when the agents report more.
const autonomyLadder = <(String, String, bool)>[
  ('Does it see that somebody is using the computer?', 'Lock, display and screen saver only. No typing, mouse or meeting detection.', true),
  ('Does it check its own readiness?', 'Signature and identity. No battery, disk space or running-app checks.', true),
  ('Does it learn a patch window per host?', 'One global Patching Policy for everybody.', false),
  ('Does it make the call?', 'Holds, prompts and patches within the policy. The delay budget forces it through.', true),
];

const notMeasuredYet = <String>[
  'A patch held because the screen was locked, the display asleep or a screen saver running.',
  'A person pressing Delay, a prompt nobody answered, or the delay budget running out.',
  'Why a host skipped a cycle (no signed script, server unreachable).',
];

/// The two trends the report draws, by KPI id.
const trendKpis = <String>['failures.open_failure_devices', 'patching.os_update_waiting'];
