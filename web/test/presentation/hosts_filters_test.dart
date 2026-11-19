import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/widgets/kintsugi_table.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_filters.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_format.dart';

final _now = DateTime.utc(2026, 11, 16, 12);

/// A host with only what a test cares about; the rest is a plain, healthy, connected machine.
HostSummary host(
  String name, {
  HostStatus status = HostStatus.online,
  DateTime? seen,
  String? department,
  String? location,
  String? model,
  String? os = 'Windows 11',
  HostHealthState health = HostHealthState.healthy,
  int updates = 0,
  int cves = 0,
  int cvesFixed = 0,
  int failed = 0,
  String? serial,
  String? ip,
}) =>
    HostSummary(
      id: 'id-$name',
      hostname: name,
      serialNumber: serial ?? 'S-$name',
      operatingSystem: os,
      ipAddress: ip,
      status: status,
      lastSeenUtc: seen,
      agentVersion: null,
      operatingSystemUpdateAvailable: false,
      operatingSystemLatestVersion: null,
      appUpdatesAvailableCount: updates,
      unpatchedCveCount: cves,
      patchedCveCount: cvesFixed,
      removalRequested: false,
      department: department,
      location: location,
      model: model,
      health: health,
      failedPatchCount: failed,
    );

List<String> names(Iterable<HostSummary> hosts) => [for (final h in hosts) h.hostname];

void main() {
  group('what each field says about a host', () {
    test('the two-option fields have exactly their two values', () {
      expect(HostFilterField.connection.fixedValues, ['Online', 'Not online']);
      expect(HostFilterField.updates.fixedValues, ['With updates', 'None']);
      expect(HostFilterField.cves.fixedValues, ['With CVEs', 'None']);
      expect(HostFilterField.departmentSet.fixedValues, ['Set', 'Not set']);
      expect(HostFilterField.locationSet.fixedValues, ['Set', 'Not set']);
      for (final field in HostFilterField.values.where((f) => f.isTwoOption)) {
        expect(field.fixedValues, hasLength(2), reason: field.label);
      }
      expect(HostFilterField.values.where((f) => f.isTwoOption), hasLength(5));
    });

    test('are read from the host', () {
      final h = host('a', department: 'HR', location: 'Perth', model: 'Surface Pro 8', updates: 2, cves: 1, health: HostHealthState.pending);

      expect(HostFilterField.connection.valueOf(h), 'Online');
      expect(HostFilterField.connection.valueOf(host('b', status: HostStatus.offline)), 'Not online');
      expect(HostFilterField.connection.valueOf(host('c', status: HostStatus.unknown)), 'Not online');
      expect(HostFilterField.updates.valueOf(h), 'With updates');
      expect(HostFilterField.updates.valueOf(host('d')), 'None');
      expect(HostFilterField.cves.valueOf(h), 'With CVEs');
      // Only the patchable CVEs count: a CVE an update cannot clear is not something to chase.
      expect(HostFilterField.cves.valueOf(host('e', cvesFixed: 4)), 'None');
      expect(HostFilterField.departmentSet.valueOf(h), 'Set');
      expect(HostFilterField.departmentSet.valueOf(host('f')), 'Not set');
      expect(HostFilterField.locationSet.valueOf(host('g')), 'Not set');
      expect(HostFilterField.department.valueOf(h), 'HR');
      expect(HostFilterField.department.valueOf(host('h')), hostNotSet);
      expect(HostFilterField.model.valueOf(h), 'Surface Pro 8');
      expect(HostFilterField.health.valueOf(h), 'Pending');
      expect(HostFilterField.status.valueOf(h), 'Online');
    });

    test('the system is Windows, macOS or Linux, as the server works it out', () {
      expect(hostSystem(host('a', os: 'Windows 11 Pro 26H2 (26300)')), 'Windows');
      expect(hostSystem(host('b', os: 'macOS 15.1')), 'macOS');
      expect(hostSystem(host('c', os: 'Darwin 24')), 'macOS');
      expect(hostSystem(host('d', os: 'Ubuntu 24.04')), 'Linux');
      expect(hostSystem(host('e', os: null)), '(unknown)');
    });
  });

  group('the filters', () {
    final hosts = [
      host('a', department: 'Technology', location: 'Sydney', status: HostStatus.online),
      host('b', department: 'HR', location: 'Perth', status: HostStatus.offline),
      host('c', department: 'HR', location: 'Sydney', status: HostStatus.online),
      host('d', status: HostStatus.offline),
    ];

    List<String> run(HostFilters filters) => names(hosts.where((h) => filters.matches(h)));

    test('with none chosen leave every host', () {
      expect(const HostFilters().isEmpty, isTrue);
      expect(run(const HostFilters()), ['a', 'b', 'c', 'd']);
    });

    test('are OR inside one field', () {
      final filters = const HostFilters().withValues(HostFilterField.department, {'Technology', 'HR'});

      expect(run(filters), ['a', 'b', 'c']);
    });

    test('are AND across fields', () {
      final filters = const HostFilters()
          .withValues(HostFilterField.department, {'Technology', 'HR'})
          .withValues(HostFilterField.location, {'Sydney'});

      expect(run(filters), ['a', 'c']);
    });

    test('a field with no values is not filtering, and its chip goes', () {
      var filters = const HostFilters().withValues(HostFilterField.department, {'HR'});
      expect(filters.isActive(HostFilterField.department), isTrue);

      filters = filters.withValues(HostFilterField.department, const {});

      expect(filters.isActive(HostFilterField.department), isFalse);
      expect(filters.isEmpty, isTrue);
    });

    test('"Not set" can be chosen like any department', () {
      final filters = const HostFilters().withValues(HostFilterField.department, {hostNotSet});

      expect(run(filters), ['d']);
    });

    test('the two-option fields work like any other', () {
      expect(run(const HostFilters().withValues(HostFilterField.connection, {'Not online'})), ['b', 'd']);
      expect(run(const HostFilters().withValues(HostFilterField.departmentSet, {'Not set'})), ['d']);
    });

    test('list their active fields in a fixed order, so a chip never jumps', () {
      final a = const HostFilters().withValues(HostFilterField.health, {'Problem'}).withValues(HostFilterField.department, {'HR'});
      final b = const HostFilters().withValues(HostFilterField.department, {'HR'}).withValues(HostFilterField.health, {'Problem'});

      expect(a.activeFields, [HostFilterField.department, HostFilterField.health]);
      expect(b.activeFields, a.activeFields);
      expect(a, b);
    });

    test('toggling adds a value and takes it out again', () {
      var filters = const HostFilters().toggled(HostFilterField.department, 'HR');
      filters = filters.toggled(HostFilterField.department, 'Technology');
      expect(filters.valuesOf(HostFilterField.department), {'HR', 'Technology'});

      filters = filters.toggled(HostFilterField.department, 'HR').toggled(HostFilterField.department, 'Technology');
      expect(filters.isEmpty, isTrue);
    });

    test('ignore the fields they are told to, for the chart', () {
      final filters = const HostFilters()
          .withValues(HostFilterField.department, {'HR'})
          .withValues(HostFilterField.health, {'Problem'});

      expect(filters.matches(host('x', department: 'Technology'), except: {HostFilterField.department, HostFilterField.health}), isTrue);
      expect(filters.matches(host('x', department: 'Technology')), isFalse);
    });

    test('say what they list: "HR · Pending", or "All hosts"', () {
      expect(const HostFilters().summaryLabel, 'All hosts');
      expect(
        const HostFilters().segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending).summaryLabel,
        'HR · Pending',
      );
    });
  });

  group('a click on a coloured part of a bar', () {
    test('applies the group and the health state together, and nothing else', () {
      final before = const HostFilters().withValues(HostFilterField.location, {'Perth'});

      final after = before.segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending);

      expect(after.valuesOf(HostFilterField.department), {'HR'});
      expect(after.valuesOf(HostFilterField.health), {'Pending'});
      expect(after.valuesOf(HostFilterField.location), {'Perth'}, reason: 'other filters are left alone');
      expect(after.activeFields, [HostFilterField.department, HostFilterField.location, HostFilterField.health]);
    });

    test('replaces whatever the group and health fields held', () {
      final before = const HostFilters()
          .withValues(HostFilterField.department, {'Technology', 'HR'})
          .withValues(HostFilterField.health, {'Problem', 'Healthy'});

      final after = before.segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending);

      expect(after.valuesOf(HostFilterField.department), {'HR'});
      expect(after.valuesOf(HostFilterField.health), {'Pending'});
    });

    test('knows when that part is already applied, and a second click removes both chips', () {
      final applied = const HostFilters().segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending);

      expect(applied.segmentActive(HostFilterField.department, 'HR', HostHealthState.pending), isTrue);
      expect(applied.segmentActive(HostFilterField.department, 'HR', HostHealthState.problem), isFalse);
      expect(applied.segmentActive(HostFilterField.department, 'Technology', HostHealthState.pending), isFalse);

      final cleared = applied.segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending);
      expect(cleared.isEmpty, isTrue);
    });

    test('clearing it leaves the other filters in place', () {
      final applied = const HostFilters()
          .withValues(HostFilterField.location, {'Perth'})
          .segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending);

      final cleared = applied.segmentToggled(HostFilterField.department, 'HR', HostHealthState.pending);

      expect(cleared.valuesOf(HostFilterField.location), {'Perth'});
      expect(cleared.activeFields, [HostFilterField.location]);
    });

    test('a click on the group\'s name applies the group alone', () {
      final filters = const HostFilters().toggled(HostFilterField.department, 'HR');

      expect(filters.activeFields, [HostFilterField.department]);
      expect(filters.isActive(HostFilterField.health), isFalse);
    });
  });

  group('counting the values of a field', () {
    final hosts = [
      host('a', department: 'HR'),
      host('b', department: 'HR'),
      host('c', department: 'Technology'),
      host('d'),
    ];

    test('lists a two-option field in its fixed order, zeros included, so both options are always there', () {
      final entries = countHostValues(HostFilterField.connection, hosts.map((h) => h));
      expect(entries.map((e) => e.key), ['Online', 'Not online']);
      expect(entries.map((e) => e.value), [4, 0]);
    });

    test('lists the others by count, then name', () {
      final entries = countHostValues(HostFilterField.department, hosts);

      expect(entries.map((e) => '${e.key}:${e.value}'), ['HR:2', 'Not set:1', 'Technology:1']);
    });
  });

  group('a host with no model', () {
    final hosts = [
      host('a', model: 'Surface Pro 8', health: HostHealthState.problem),
      host('b', model: 'Surface Pro 8'),
      host('c', model: 'Dell Latitude 5440'),
      host('d'),
      host('e'),
    ];

    test('is not a group of "Where it fails": it is left out of the bars rather than listed as "Not set"', () {
      final groups = groupHostsByHealth(hosts, HostFilterField.model);

      expect(groups.map((g) => g.key), ['Surface Pro 8', 'Dell Latitude 5440']);
      expect(groups.fold<int>(0, (sum, g) => sum + g.total), 3, reason: 'the two without a model are not counted anywhere');
    });

    test('is not a value of the Model filter either', () {
      final entries = countHostValues(HostFilterField.model, hosts);

      expect(entries.map((e) => e.key), ['Surface Pro 8', 'Dell Latitude 5440']);
      expect(entries.map((e) => e.key), isNot(contains(hostNotSet)));
    });

    test('is still a value of the other fields: a missing department is something to find', () {
      expect(HostFilterField.department.listsNotSet, isTrue);
      expect(HostFilterField.location.listsNotSet, isTrue);
      expect(HostFilterField.model.listsNotSet, isFalse);
      expect(countHostValues(HostFilterField.department, hosts).map((e) => e.key), [hostNotSet]);
    });

    test('goes to the bottom when the table is sorted by model', () {
      expect(names(sortHosts(hosts, const TableSort(HostSortKey.model))), ['c', 'a', 'b', 'd', 'e']);
    });
  });

  group('grouping for the bars', () {
    final hosts = [
      host('a', department: 'Residential', health: HostHealthState.problem),
      host('b', department: 'Residential', health: HostHealthState.pending),
      host('c', department: 'Residential', health: HostHealthState.healthy),
      host('d', department: 'Technology', health: HostHealthState.healthy),
      host('e', department: 'Technology', health: HostHealthState.healthy),
      host('f', department: 'HR', health: HostHealthState.pending),
      host('g'),
    ];

    test('count each group\'s three states, worst first', () {
      final groups = groupHostsByHealth(hosts, HostFilterField.department);

      expect(groups.map((g) => g.key), ['Residential', 'HR', 'Technology', 'Not set']);
      final residential = groups.first;
      expect([residential.healthy, residential.pending, residential.problem, residential.total], [1, 1, 1, 3]);
    });

    test('say the numbers on the hover text of a segment', () {
      final residential = groupHostsByHealth(hosts, HostFilterField.department).first;

      expect(residential.tooltip(HostHealthState.problem), 'Residential · Problem: 1 of 3 hosts (33%)');
      expect(residential.tooltip(HostHealthState.healthy), 'Residential · Healthy: 1 of 3 hosts (33%)');
    });

    test('say "host" for one, and round the share', () {
      final notSet = groupHostsByHealth(hosts, HostFilterField.department).last;

      expect(notSet.tooltip(HostHealthState.healthy), 'Not set · Healthy: 1 of 1 host (100%)');
      final hr = groupHostsByHealth(hosts, HostFilterField.department).firstWhere((g) => g.key == 'HR');
      expect(hr.tooltip(HostHealthState.pending), 'HR · Pending: 1 of 1 host (100%)');
    });

    test('group by any of the four fields', () {
      expect(HostGroupBy.values.map((g) => g.field), [
        HostFilterField.department,
        HostFilterField.location,
        HostFilterField.system,
        HostFilterField.model,
      ]);
    });
  });

  group('the order of the table', () {
    List<String> sorted(List<HostSummary> hosts, String key, {bool ascending = true}) =>
        names(sortHosts(hosts, TableSort(key, ascending: ascending)));

    test('opens with connected hosts first, then the rest by how recently they checked in', () {
      final hosts = [
        host('d-old', status: HostStatus.offline, seen: _now.subtract(const Duration(days: 40))),
        host('a-online-10m', seen: _now.subtract(const Duration(minutes: 10))),
        host('c-offline-3d', status: HostStatus.offline, seen: _now.subtract(const Duration(days: 3))),
        host('b-online-2m', seen: _now.subtract(const Duration(minutes: 2))),
        host('e-never', status: HostStatus.offline),
        host('f-offline-5m', status: HostStatus.offline, seen: _now.subtract(const Duration(minutes: 5))),
      ];

      expect(hostDefaultSort.key, HostSortKey.lastUptime);
      expect(hostDefaultSort.ascending, isTrue, reason: 'its up-arrow shows when the page opens');
      expect(names(sortHosts(hosts, hostDefaultSort)), [
        'b-online-2m',
        'a-online-10m',
        'f-offline-5m',
        'c-offline-3d',
        'd-old',
        'e-never',
      ]);
    });

    test('Status sorts the same way as Last uptime', () {
      final hosts = [
        host('x', status: HostStatus.offline, seen: _now),
        host('y', seen: _now.subtract(const Duration(hours: 1))),
      ];

      expect(sorted(hosts, HostSortKey.status), sorted(hosts, HostSortKey.lastUptime));
      expect(sorted(hosts, HostSortKey.status), ['y', 'x']);
    });

    test('pressing the header again reverses it', () {
      final hosts = [
        host('a', seen: _now),
        host('b', status: HostStatus.offline, seen: _now),
      ];

      expect(sorted(hosts, HostSortKey.lastUptime, ascending: true), ['a', 'b']);
      expect(sorted(hosts, HostSortKey.lastUptime, ascending: false), ['b', 'a']);
    });

    test('Location is alphabetical by city, case-insensitive, with hosts that have none last', () {
      final hosts = [
        host('h1', location: 'Sydney'),
        host('h2', location: 'adelaide'),
        host('h3'),
        host('h4', location: 'Perth'),
        host('h5', location: 'Brisbane'),
      ];

      expect(sorted(hosts, HostSortKey.location), ['h2', 'h5', 'h4', 'h1', 'h3']);
    });

    test('Department is alphabetical with hosts that have none last', () {
      final hosts = [
        host('h1', department: 'Technology'),
        host('h2'),
        host('h3', department: 'HR'),
        host('h4', department: 'Residential'),
      ];

      expect(sorted(hosts, HostSortKey.department), ['h3', 'h4', 'h1', 'h2']);
    });

    test('Health is worst first: Problem, then Pending, then Healthy', () {
      final hosts = [
        host('c', health: HostHealthState.healthy),
        host('b', health: HostHealthState.pending),
        host('a', health: HostHealthState.problem),
        host('d', health: HostHealthState.healthy),
      ];

      expect(sorted(hosts, HostSortKey.health), ['a', 'b', 'c', 'd']);
      expect(sorted(hosts, HostSortKey.health, ascending: false), ['c', 'd', 'b', 'a']);
    });

    test('Hostname, Serial and Model are alphabetical', () {
      final hosts = [
        host('Charlie', serial: 'S-3', model: 'Surface Pro 8'),
        host('alpha', serial: 'S-2', model: 'Dell Latitude 5440'),
        host('Bravo', serial: 'S-1', model: null),
      ];

      expect(sorted(hosts, HostSortKey.hostname), ['alpha', 'Bravo', 'Charlie']);
      expect(sorted(hosts, HostSortKey.serial), ['Bravo', 'alpha', 'Charlie']);
      expect(sorted(hosts, HostSortKey.model), ['alpha', 'Charlie', 'Bravo'], reason: 'no model goes last');
    });

    test('Updates and CVEs are highest count first', () {
      final hosts = [
        host('a', updates: 1, cves: 5, cvesFixed: 0, failed: 0),
        host('b', updates: 7, cves: 0, cvesFixed: 9, failed: 2),
        host('c', updates: 3, cves: 2, cvesFixed: 1, failed: 5),
      ];

      expect(sorted(hosts, HostSortKey.updates), ['b', 'c', 'a']);
      // The one CVEs column sorts by the count that is an alert (not confirmed fixed).
      expect(sorted(hosts, HostSortKey.cves), ['a', 'c', 'b']);
      expect(sorted(hosts, HostSortKey.updates, ascending: false), ['a', 'c', 'b']);
    });

    test('ties are broken by hostname, so rows never jump and a refresh never shuffles them', () {
      final hosts = [
        host('delta', location: 'Perth', updates: 2),
        host('Bravo', location: 'Perth', updates: 2),
        host('charlie', location: 'Perth', updates: 2),
        host('alpha', location: 'Perth', updates: 2),
      ];

      for (final key in [HostSortKey.location, HostSortKey.updates, HostSortKey.health, HostSortKey.department]) {
        expect(sorted(hosts, key), ['alpha', 'Bravo', 'charlie', 'delta'], reason: key);
        // The tie-break keeps its own direction when the column is reversed.
        expect(sorted(hosts, key, ascending: false), ['alpha', 'Bravo', 'charlie', 'delta'], reason: '$key reversed');
        // However the list arrives, the answer is the same.
        expect(sorted(hosts.reversed.toList(), key), ['alpha', 'Bravo', 'charlie', 'delta'], reason: '$key reshuffled');
      }
    });

    test('does not change the list it is given', () {
      final hosts = [host('b'), host('a')];

      sortHosts(hosts, const TableSort(HostSortKey.hostname));

      expect(names(hosts), ['b', 'a']);
    });
  });

  group('how long ago', () {
    String? ago(Duration d) => hostRelativeAgo(_now.subtract(d), now: _now);

    test('says it the way NinjaOne does', () {
      expect(hostRelativeAgo(null), isNull);
      expect(ago(Duration.zero), 'just now');
      expect(ago(const Duration(minutes: 1)), 'a minute ago');
      expect(ago(const Duration(minutes: 28)), '28 minutes ago');
      expect(ago(const Duration(hours: 1)), 'an hour ago');
      expect(ago(const Duration(hours: 9)), '9 hours ago');
      expect(ago(const Duration(days: 1)), 'a day ago');
      expect(ago(const Duration(days: 3)), '3 days ago');
      expect(ago(const Duration(days: 35)), 'a month ago');
      expect(ago(const Duration(days: 100)), '3 months ago');
      expect(ago(const Duration(days: 400)), 'a year ago');
    });
  });
}
