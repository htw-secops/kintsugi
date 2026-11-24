import 'package:intl/intl.dart';

import '../../domain/entities/autonomy.dart';
import '../../domain/entities/host_activity.dart';

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

/// How the change reads, for colouring its cell: "G" when the figure moved the good way, "R" when it
/// moved the bad way, "I" (no colour) when it did not move, has no prior, or is only information.
/// Which way is good depends on the KPI: fewer waiting devices is good, more devices checking in is.
String kpiChangeStatus(AutonomyKpi k) {
  final d = k.delta;
  if (d == null || d == 0) return 'I';
  return switch (k.direction) {
    'LowerIsBetter' => d < 0 ? 'G' : 'R',
    'HigherIsBetter' => d > 0 ? 'G' : 'R',
    _ => 'I',
  };
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

/// What Kintsugi itself did on a device close to a health event, so "was it us?" has an answer on the
/// page. It is the evidence an investigation otherwise has to assemble by hand: a patch, a Patch now, or
/// nothing at all within [window] either side of the event.
String kintsugiNear(HostEvent event, List<HostEvent> all, {Duration window = const Duration(minutes: 15)}) {
  bool ours(HostEvent e) => switch (e.kind) {
        HostEventKind.applicationPatched ||
        HostEventKind.operatingSystemPatched ||
        HostEventKind.patchNowRequested ||
        HostEventKind.patchNowPickedUp =>
          true,
        _ => false,
      };

  final near = all.where((e) => ours(e) && e.occurredUtc.difference(event.occurredUtc).abs() <= window).toList()
    ..sort((a, b) => a.occurredUtc.difference(event.occurredUtc).abs().compareTo(b.occurredUtc.difference(event.occurredUtc).abs()));
  if (near.isEmpty) return 'No Kintsugi action within ${window.inMinutes} minutes';

  final e = near.first;
  final minutes = e.occurredUtc.difference(event.occurredUtc).inMinutes.abs();
  final when = e.occurredUtc.isBefore(event.occurredUtc) ? 'before' : 'after';
  return 'Kintsugi: ${e.summary} ($minutes minute${minutes == 1 ? '' : 's'} $when)';
}

/// The rule that puts a device in the list, in words. Fixed, and the same on the page and in the report.
const deviceHealthRule = 'Unstable: 3 or more unexpected shutdowns in 7 days, or any fatal hardware error in 30 days. '
    'Watch: 1 or 2 unexpected shutdowns in 30 days, or a corrected hardware error. It is per device and over time, '
    'never a fleet count: a few unexpected shutdowns a day across a large fleet are normal. A restart that '
    'the operating system, a person or Kintsugi asked for is not one.';

/// What a slice of an "at a glance" ring means. The widget gives each kind a colour; the words are in
/// the label, so the colour is never the only signal.
enum RingKind { good, waiting, bad, other }

class RingSlice {
  const RingSlice(this.label, this.value, this.kind);

  final String label;
  final double value;
  final RingKind kind;
}

/// One ring card on the Autonomy page: a number in the middle, slices around it.
class RingData {
  const RingData({required this.title, required this.subtitle, required this.centre, required this.note, required this.slices});

  final String title;
  final String subtitle;
  final String centre;
  final String note;
  final List<RingSlice> slices;

  double get total => slices.fold(0, (a, s) => a + s.value);
}

/// The three rings above the causes: devices patched or failing, device health, and what the delay costs.
/// Everything is read from [Autonomy]; nothing is estimated.
List<RingData> ringsFor(Autonomy data) {
  final total = data.totalHosts;
  final delayed = data.devicesDelayed.clamp(0, total);
  final failing = data.failedDevices.clamp(0, delayed);
  final waiting = delayed - failing;
  final upToDate = total - delayed;

  final health = data.deviceHealth;
  final flagged = (health.unstable + health.watch).clamp(0, total);
  final healthy = (total - health.unstable - health.watch).clamp(0, total);

  final causes = data.causes.where((c) => c.measured && c.deviceDays > 0).toList()..sort((a, b) => b.deviceDays.compareTo(a.deviceDays));
  final kinds = [RingKind.bad, RingKind.waiting, RingKind.good, RingKind.other];
  final shown = causes.take(3).toList();
  final rest = causes.skip(3).fold<double>(0, (a, c) => a + c.deviceDays);
  String days(double v) => v == v.roundToDouble() ? '${v.toInt()}' : v.toStringAsFixed(1);

  return [
    RingData(
      title: 'Patching',
      subtitle: 'Devices up to date, waiting or failing',
      centre: total == 0 ? '—' : '${(upToDate * 100 / total).round()}%',
      note: total == 0 ? 'no devices yet' : '$upToDate of $total up to date',
      slices: [
        RingSlice('Up to date', upToDate.toDouble(), RingKind.good),
        RingSlice('Waiting (offline, restart)', waiting.toDouble(), RingKind.waiting),
        RingSlice('Failing', failing.toDouble(), RingKind.bad),
      ],
    ),
    RingData(
      title: 'Device health',
      subtitle: 'Unstable devices, from each device\'s own log',
      centre: total == 0 ? '—' : '$flagged',
      note: flagged == 0 ? 'none need a look' : 'need a look',
      slices: [
        RingSlice('Healthy', healthy.toDouble(), RingKind.good),
        RingSlice('Watch', health.watch.toDouble(), RingKind.waiting),
        RingSlice('Unstable', health.unstable.toDouble(), RingKind.bad),
      ],
    ),
    RingData(
      title: 'Cost of slow patching',
      subtitle: 'Device-days of waiting, by cause',
      centre: days(data.totalDeviceDays.roundToDouble()),
      note: 'device-days lost',
      slices: [
        for (var i = 0; i < shown.length; i++) RingSlice(shown[i].title, shown[i].deviceDays, kinds[i]),
        if (rest > 0) RingSlice('Other causes', rest, RingKind.other),
      ],
    ),
  ];
}

/// A device's warranty, as Snipe-IT will report it. PENDING: nothing fills this in yet, so the page says
/// "pending" for every device rather than guessing.
///
/// A warranty needs both: Snipe-IT connected, AND the host matched to its Snipe-IT asset. A host with no asset
/// there (or an asset with no date) stays "pending" even once Snipe-IT is connected.
///
/// To finish it, whoever connects Snipe-IT needs to: (1) read each asset's warranty end date, matched to a
/// host by serial number; (2) hand `AutonomyScreen` a `Map<hostId, DeviceWarranty>` (it is passed to
/// `_DeviceHealthPanel.warranties`, empty today); (3) leave [endsUtc] null for an asset with no date.
/// `warrantyAdvice` below already turns a date into the advice, and is tested.
class DeviceWarranty {
  const DeviceWarranty({this.endsUtc});

  final DateTime? endsUtc;
}

enum WarrantyTone { claim, urgent, expired, watch, unknown }

/// What to do about a flagged device, from its warranty. A claim only works while the warranty has not
/// expired, so the date decides the advice: an unstable device in warranty is a claim, one out of it is a
/// purchase, and a device only on watch is left alone until it gets worse.
({String text, WarrantyTone tone}) warrantyAdvice(AutonomyHealthDevice device, DeviceWarranty? warranty, DateTime now) {
  final ends = warranty?.endsUtc;
  if (ends == null) return (text: 'Warranty: pending (Snipe-IT is not connected yet)', tone: WarrantyTone.unknown);

  final left = ends.difference(now.toUtc()).inDays;
  final date = DateFormat('yyyy-MM-dd').format(ends.toLocal());
  if (left < 0) {
    return device.isUnstable
        ? (text: 'Warranty ended $date, ${-left} days ago: it cannot be claimed. Order a new one.', tone: WarrantyTone.expired)
        : (text: 'Warranty ended $date. Replace it if it gets worse.', tone: WarrantyTone.expired);
  }
  if (!device.isUnstable) return (text: 'Warranty ends $date ($left days). Claim it if it keeps failing.', tone: WarrantyTone.watch);
  return left <= 30
      ? (text: 'Ask for the warranty now: it ends $date ($left days).', tone: WarrantyTone.urgent)
      : (text: 'Ask for the warranty: it ends $date ($left days).', tone: WarrantyTone.claim);
}
