import 'package:equatable/equatable.dart';

import '../../core/widgets/kintsugi_table.dart';
import '../../domain/entities/host.dart';

/// What a text field shows for a host that has no value for it. A filter value rather than an empty
/// string, so "hosts with no department" can be chosen like any other department.
const hostNotSet = 'Not set';

/// "Windows", "macOS" or "Linux", worked out from the operating-system text the same way the server's
/// `DevicePlatform.Of` does, so the System filter and the report page agree on what a platform is.
String hostSystem(HostSummary host) {
  final os = (host.operatingSystem ?? '').toLowerCase();
  if (os.isEmpty) return '(unknown)';
  if (os.contains('windows')) return 'Windows';
  if (os.contains('mac') || os.contains('darwin')) return 'macOS';
  return 'Linux';
}

/// Every field the administrator can filter by. Chosen from "+ Add filter" and shown as chips.
///
/// The first five are **two-option** fields: they have exactly two values, listed in a fixed order,
/// and exist so a common question ("which are offline?", "which have CVEs?") is two clicks. The rest
/// have as many values as the fleet has, listed by how many hosts carry each.
enum HostFilterField {
  connection('Connection', ['Online', 'Not online']),
  updates('Updates', ['With updates', 'None']),
  cves('CVEs', ['With CVEs', 'None']),
  departmentSet('Department set', ['Set', 'Not set']),
  locationSet('Location set', ['Set', 'Not set']),
  organization('Organization'),
  department('Department'),
  location('Location'),
  system('System'),
  model('Model'),
  status('Status'),
  health('Health');

  const HostFilterField(this.label, [this.fixedValues]);

  final String label;

  /// The two values of a two-option field, in the order they are offered; null for the others.
  final List<String>? fixedValues;

  bool get isTwoOption => fixedValues != null;

  /// Whether "Not set" is offered as a value of this field, and as a bar of "Where it fails".
  ///
  /// False for the model, and on purpose. Nothing fills a host's model yet: it comes from an import
  /// of the Snipe-IT export, and no agent reports it. Until one does, almost every host has none, and
  /// a "Not set" bar or filter value would be the biggest thing on the page and say nothing. So a host
  /// with no model is simply not in the model's groups or value lists, and its cell is left blank.
  bool get listsNotSet => this != HostFilterField.model;

  /// What [host] has for this field, as the string the filter compares and the chip shows.
  String valueOf(HostSummary host) => switch (this) {
        HostFilterField.connection => host.isConnected ? 'Online' : 'Not online',
        HostFilterField.updates => host.appUpdatesAvailableCount > 0 ? 'With updates' : 'None',
        // The patchable column: CVEs a patch would clear. The other column (what an update cannot
        // fix) is information, not something to chase.
        HostFilterField.cves => host.unpatchedCveCount > 0 ? 'With CVEs' : 'None',
        HostFilterField.departmentSet => host.department != null ? 'Set' : 'Not set',
        HostFilterField.locationSet => host.location != null ? 'Set' : 'Not set',
        HostFilterField.organization => host.organization ?? hostNotSet,
        HostFilterField.department => host.department ?? hostNotSet,
        HostFilterField.location => host.location ?? hostNotSet,
        HostFilterField.system => hostSystem(host),
        HostFilterField.model => host.model ?? hostNotSet,
        HostFilterField.status => host.status.label,
        HostFilterField.health => host.health.label,
      };
}

/// The filters the administrator has chosen: for each field, the values it may have.
///
/// **OR inside one field, AND across fields**: Department = Technology *or* HR, and Location =
/// Sydney, is "(Technology or HR) in Sydney". A field with no values chosen is not filtering at all,
/// which is why choosing none removes the chip rather than matching nothing.
class HostFilters extends Equatable {
  const HostFilters([this.selected = const {}]);

  final Map<HostFilterField, Set<String>> selected;

  bool get isEmpty => selected.isEmpty;

  /// The fields that are filtering, in the order the chips are drawn (declaration order, so a chip
  /// never jumps because another was added).
  List<HostFilterField> get activeFields => [
        for (final field in HostFilterField.values)
          if (selected.containsKey(field)) field,
      ];

  Set<String> valuesOf(HostFilterField field) => selected[field] ?? const {};

  bool isActive(HostFilterField field) => selected.containsKey(field);

  /// This field set to exactly [values]; none removes it.
  HostFilters withValues(HostFilterField field, Iterable<String> values) {
    final next = {...selected};
    final set = values.toSet();
    if (set.isEmpty) {
      next.remove(field);
    } else {
      next[field] = set;
    }
    return HostFilters(next);
  }

  HostFilters without(HostFilterField field) => withValues(field, const []);

  /// Adds [value] to the field's values, or takes it out when it is already there. How a click on a
  /// group's name works: Department = HR, click Technology too, and both are chosen.
  HostFilters toggled(HostFilterField field, String value) {
    final current = valuesOf(field);
    return withValues(field, current.contains(value) ? current.difference({value}) : {...current, value});
  }

  /// Whether a click on the [health] part of [group]'s bar is already applied: the group field is
  /// exactly that one value and the health field is exactly that one state.
  bool segmentActive(HostFilterField groupField, String group, HostHealthState health) =>
      valuesOf(groupField).length == 1 &&
      valuesOf(groupField).first == group &&
      valuesOf(HostFilterField.health).length == 1 &&
      valuesOf(HostFilterField.health).first == health.label;

  /// A click on a coloured part of a bar. Applies **both** the group and the health state, replacing
  /// whatever those two fields held, and leaves every other filter alone. Clicking the same part
  /// again removes both, so the same press that narrows the table also undoes it.
  HostFilters segmentToggled(HostFilterField groupField, String group, HostHealthState health) {
    if (segmentActive(groupField, group, health)) {
      return without(groupField).without(HostFilterField.health);
    }
    return withValues(groupField, {group}).withValues(HostFilterField.health, {health.label});
  }

  /// "HR · Pending": the chosen values in chip order, for the line above the table that says what is
  /// being listed. "All hosts" when nothing is chosen.
  String get summaryLabel {
    if (isEmpty) return 'All hosts';
    return activeFields.map((field) => (valuesOf(field).toList()..sort()).join(', ')).join(' · ');
  }

  /// Whether [host] passes every field, except the ones in [except] (which are ignored). The chart
  /// leaves out the field it groups by and the health field so its bars keep their context while a
  /// segment is applied.
  bool matches(HostSummary host, {Set<HostFilterField> except = const {}}) {
    for (final entry in selected.entries) {
      if (except.contains(entry.key)) continue;
      if (!entry.value.contains(entry.key.valueOf(host))) return false;
    }
    return true;
  }

  @override
  List<Object?> get props => [selected];
}

/// How many of [hosts] have each value of [field], in the order the menu lists them: a two-option
/// field in its fixed order (zeros included, so both options are always there), the others by count
/// then name.
List<MapEntry<String, int>> countHostValues(HostFilterField field, Iterable<HostSummary> hosts) {
  final counts = <String, int>{};
  for (final host in hosts) {
    final value = field.valueOf(host);
    if (value == hostNotSet && !field.listsNotSet) continue;
    counts[value] = (counts[value] ?? 0) + 1;
  }
  final fixed = field.fixedValues;
  if (fixed != null) return [for (final value in fixed) MapEntry(value, counts[value] ?? 0)];

  final entries = counts.entries.toList()
    ..sort((a, b) {
      final byCount = b.value.compareTo(a.value);
      return byCount != 0 ? byCount : a.key.toLowerCase().compareTo(b.key.toLowerCase());
    });
  return entries;
}

/// What "Where it fails" can group by. Each is a [HostFilterField], so a click on a bar's name or a
/// coloured part becomes an ordinary filter chip.
enum HostGroupBy {
  department('Department', HostFilterField.department),
  location('Location', HostFilterField.location),
  system('System', HostFilterField.system),
  model('Model', HostFilterField.model);

  const HostGroupBy(this.label, this.field);

  final String label;
  final HostFilterField field;
}

/// One bar of "Where it fails": how many hosts of a group are in each of the three states.
class HostGroup extends Equatable {
  const HostGroup({required this.key, required this.healthy, required this.pending, required this.problem});

  final String key;
  final int healthy;
  final int pending;
  final int problem;

  int get total => healthy + pending + problem;

  int countOf(HostHealthState state) => switch (state) {
        HostHealthState.healthy => healthy,
        HostHealthState.pending => pending,
        HostHealthState.problem => problem,
      };

  /// "Residential · Problem: 12 of 33 hosts (36%)", the text on a segment's tooltip.
  String tooltip(HostHealthState state) {
    final count = countOf(state);
    final percent = total == 0 ? 0 : (count / total * 100).round();
    return '$key · ${state.label}: $count of $total host${total == 1 ? '' : 's'} ($percent%)';
  }

  @override
  List<Object?> get props => [key, healthy, pending, problem];
}

/// Groups [hosts] by [field] and counts each group's three states. Worst first (most problems, then
/// most pending, then biggest), so the bar that most needs a look is at the top.
List<HostGroup> groupHostsByHealth(Iterable<HostSummary> hosts, HostFilterField field) {
  final buckets = <String, List<int>>{};
  for (final host in hosts) {
    final value = field.valueOf(host);
    if (value == hostNotSet && !field.listsNotSet) continue;
    final counts = buckets.putIfAbsent(value, () => [0, 0, 0]);
    counts[host.health.index]++;
  }
  final groups = [
    for (final entry in buckets.entries)
      HostGroup(key: entry.key, healthy: entry.value[0], pending: entry.value[1], problem: entry.value[2]),
  ]..sort((a, b) {
      final byProblem = b.problem.compareTo(a.problem);
      if (byProblem != 0) return byProblem;
      final byPending = b.pending.compareTo(a.pending);
      if (byPending != 0) return byPending;
      final byTotal = b.total.compareTo(a.total);
      return byTotal != 0 ? byTotal : a.key.toLowerCase().compareTo(b.key.toLowerCase());
    });
  return groups;
}

/// The keys of the sortable columns. The table hands one back when its header is pressed.
abstract final class HostSortKey {
  static const hostname = 'hostname';
  static const serial = 'serial';
  static const system = 'system';
  static const model = 'model';
  static const updates = 'updates';
  // One CVEs column: the count not confirmed fixed, which is the one that is an alert.
  static const cves = 'cves';
  static const department = 'department';
  static const location = 'location';
  static const health = 'health';
  static const status = 'status';
  static const lastUptime = 'lastUptime';
}

/// How the page is ordered when it opens: connected hosts first, then the rest by most recent
/// check-in. Its arrow is the one showing on the Last uptime header.
const hostDefaultSort = TableSort(HostSortKey.lastUptime);

/// [hosts] in the order [sort] asks for. **Never arbitrary**, and never jumpy.
///
/// Every column has a *natural* order, which is what the arrow pointing up means: alphabetical for
/// text, highest first for counts, worst first for Health, connected first and then most recent for
/// Status and Last uptime. Pressing the header again reverses that primary order. Whatever the
/// column, ties are broken by hostname (then id), in the same direction every time, so two hosts
/// with the same value always keep the same places and a refresh never shuffles the table.
List<HostSummary> sortHosts(Iterable<HostSummary> hosts, TableSort sort) {
  final direction = sort.ascending ? 1 : -1;
  final list = hosts.toList();
  list.sort((a, b) {
    final primary = _natural(sort.key, a, b) * direction;
    if (primary != 0) return primary;
    final byName = a.hostname.toLowerCase().compareTo(b.hostname.toLowerCase());
    return byName != 0 ? byName : a.id.compareTo(b.id);
  });
  return list;
}

int _natural(String key, HostSummary a, HostSummary b) => switch (key) {
      HostSortKey.hostname => _text(a.hostname, b.hostname),
      HostSortKey.serial => _text(a.serialNumber, b.serialNumber),
      HostSortKey.system => _text(a.operatingSystem, b.operatingSystem),
      HostSortKey.model => _text(a.model, b.model),
      // Location is alphabetical by the location text, which is the city; hosts with none go last.
      HostSortKey.location => _text(a.location, b.location),
      HostSortKey.department => _text(a.department, b.department),
      HostSortKey.health => a.health.severity.compareTo(b.health.severity),
      HostSortKey.updates => b.appUpdatesAvailableCount.compareTo(a.appUpdatesAvailableCount),
      HostSortKey.cves => b.unpatchedCveCount.compareTo(a.unpatchedCveCount),
      HostSortKey.status || HostSortKey.lastUptime => _recency(a, b),
      _ => 0,
    };

/// Alphabetical, case-insensitive, and **empty last**: a host with nothing to show goes to the bottom
/// however the column is read, in its natural direction.
int _text(String? a, String? b) {
  final x = a?.trim().toLowerCase() ?? '';
  final y = b?.trim().toLowerCase() ?? '';
  if (x.isEmpty && y.isEmpty) return 0;
  if (x.isEmpty) return 1;
  if (y.isEmpty) return -1;
  return x.compareTo(y);
}

/// Connected hosts first, then by how recently each checked in (newest first, never-seen last).
int _recency(HostSummary a, HostSummary b) {
  if (a.isConnected != b.isConnected) return a.isConnected ? -1 : 1;
  final x = a.lastSeenUtc;
  final y = b.lastSeenUtc;
  if (x == null && y == null) return 0;
  if (x == null) return 1;
  if (y == null) return -1;
  return y.compareTo(x);
}
