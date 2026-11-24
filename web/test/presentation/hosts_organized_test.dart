import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/platform/file_picker.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/kintsugi_table.dart';
import 'package:kintsugi_web/data/models/host_activity_mapper.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/entities/host_activity.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_screen.dart';

/// A fake that records everything the screen asks of the server.
class _Repo implements HostRepository {
  _Repo(this.hosts);

  List<HostSummary> hosts;
  final revealCalls = <String>[];
  final clearCalls = <String>[];
  final maintenanceCalls = <String>[];
  final profileUpdates = <({List<String> ids, String? department, String? location})>[];
  final imports = <List<HostProfileImportRow>>[];
  ApiException? revealError;

  @override
  Future<List<HostSummary>> list() async => hosts;

  @override
  Future<void> requestRemoval(String id) async {}

  @override
  Future<AssignedPerson> assignedPerson(String id) async {
    revealCalls.add(id);
    if (revealError != null) throw revealError!;
    return const AssignedPerson(name: 'Alex Morgan', email: 'alex.morgan@example.com');
  }

  @override
  Future<void> clearAssignedPerson(String id) async => clearCalls.add(id);

  @override
  Future<void> startMaintenance(String id, {DateTime? until, String? reason}) async =>
      maintenanceCalls.add('start $id ${until == null ? 'open' : 'timed'} ${reason ?? '-'}');

  @override
  Future<void> endMaintenance(String id) async => maintenanceCalls.add('end $id');

  @override
  Future<int> updateProfiles({
    required List<String> hostIds,
    String? organization,
    String? department,
    String? location,
  }) async {
    profileUpdates.add((ids: hostIds, department: department, location: location));
    return hostIds.length;
  }

  @override
  Future<HostProfileImportResult> importProfiles(List<HostProfileImportRow> rows) async {
    imports.add(rows);
    return HostProfileImportResult(matched: rows.length - 1, notFound: 1, updated: rows.length - 1);
  }
}

class _Picker implements FilePicker {
  _Picker(this.text);

  final String text;

  @override
  Future<PickedTextFile?> pickText({required int maxBytes, String accept = '.csv,text/csv'}) async =>
      PickedTextFile(name: 'assets.csv', text: text);

  @override
  Future<Uint8List?> pick({required String accept, required int maxBytes}) => throw UnimplementedError();
}

String _ago(Duration d) => DateTime.now().toUtc().subtract(d).toIso8601String();

Map<String, dynamic> _json(
  String id,
  String name, {
  required int status,
  required Duration seen,
  String? serial,
  String? os,
  String? model,
  String? department,
  String? location,
  String health = 'Healthy',
  String? reason,
  int failed = 0,
  bool person = false,
  String? shortName,
  String? ip,
  String? agent,
  bool? osUpdate,
  int updates = 0,
  int cves = 0,
  int cvesFixed = 0,
  bool unstable = false,
}) =>
    {
      'id': id,
      'hostname': name,
      'serialNumber': serial ?? 'S-$name',
      'operatingSystem': os,
      'status': status,
      'lastSeenUtc': _ago(seen),
      'model': model,
      'department': department,
      'location': location,
      'health': health,
      'healthReason': reason,
      'failedPatchCount': failed,
      'hasAssignedPerson': person,
      'assignedShortName': shortName,
      'ipAddress': ip,
      'agentVersion': agent,
      'operatingSystemUpdateAvailable': osUpdate,
      'appUpdatesAvailableCount': updates,
      'unpatchedCveCount': cves,
      'patchedCveCount': cvesFixed,
      'unstable': unstable,
    };

/// Four hosts, built so each rule has something to show:
///  * alpha  - connected 10 min ago, Technology, Sydney, a Problem, assigned to a person;
///  * bravo  - not online, 3 days ago, HR, Adelaide, Pending;
///  * charlie - connected 2 min ago, HR, Perth, Healthy;
///  * delta  - not online, 40 days ago, no department or location, Healthy.
/// Only alpha has a model: nothing fills it yet but the Snipe-IT import.
List<HostSummary> _hosts() => [
      hostFromJson(_json('a', 'alpha',
          status: 1, seen: const Duration(minutes: 10), serial: 'A1', os: 'Windows 11 Pro', model: 'Surface Laptop 7',
          department: 'Technology', location: 'Sydney', health: 'Problem', reason: '2 failed patches',
          failed: 2, person: true, shortName: 'Alex M.', ip: '10.0.0.5', agent: '0.12.5', osUpdate: false,
          updates: 7, cves: 5, cvesFixed: 3)),
      hostFromJson(_json('b', 'bravo',
          status: 2, seen: const Duration(days: 3), serial: 'B2', os: 'Ubuntu 24.04', department: 'HR',
          location: 'Adelaide', health: 'Pending', reason: 'Agent behind')),
      hostFromJson(_json('c', 'charlie',
          status: 1, seen: const Duration(minutes: 2), serial: 'C3', os: 'macOS 15', department: 'HR', location: 'Perth',
          unstable: true)),
      hostFromJson(_json('d', 'delta', status: 2, seen: const Duration(days: 40), serial: 'D4', os: 'Windows 11')),
    ];

const _assetsCsv = 'Asset Tag,Serial,Model Name,Checked Out To,Location,Department,Email\r\n'
    'T1,S-ONE,Surface Laptop 7,Alex Morgan,Adelaide,Technology,alex.morgan@example.com\r\n'
    'T2,S-TWO,HP ProBook,Sam Taylor,Perth,HR,sam.taylor@example.com\r\n'
    'T3,S-THREE,Dell,,Sydney,HR,\r\n'
    'T4,,Dell,,Sydney,HR,\r\n';

void main() {
  late _Repo repo;
  late List<String> clipboard;

  setUp(() {
    repo = _Repo(_hosts());
    clipboard = [];
  });
  tearDown(() => locator.reset());

  /// Pumps the real screen inside a router, so a press that navigates can be seen to.
  Future<void> pump(WidgetTester tester, {Size size = const Size(1700, 2600), String csv = _assetsCsv}) async {
    locator
      ..registerSingleton(GetHosts(repo))
      ..registerSingleton(RequestHostRemoval(repo))
      ..registerSingleton(UpdateHostProfiles(repo))
      ..registerSingleton(RevealAssignedPerson(repo))
      ..registerSingleton(ClearAssignedPerson(repo))
      ..registerSingleton(SetHostMaintenance(repo))
      ..registerSingleton(ImportHostProfiles(repo))
      ..registerSingleton<FilePicker>(_Picker(csv));

    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'Clipboard.setData') clipboard.add((call.arguments as Map)['text'] as String);
      return null;
    });
    addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));

    final router = GoRouter(routes: [
      GoRoute(path: '/', builder: (_, _) => const Scaffold(body: HostsScreen())),
      GoRoute(
        path: '/hosts/:id/shell',
        builder: (_, s) => Scaffold(body: Text('SHELL ${s.pathParameters['id']} ${s.uri.queryParameters['hostname']}')),
      ),
      GoRoute(
        path: '/hosts/:id/remote',
        builder: (_, s) => Scaffold(body: Text('REMOTE ${s.pathParameters['id']} ${s.uri.queryParameters['hostname']}')),
      ),
      GoRoute(path: '/applications/failed', builder: (_, s) => Scaffold(body: Text('FAILED ${s.uri.queryParameters['host']}'))),
      GoRoute(path: '/hosts/:id/activity', builder: (_, s) => Scaffold(body: Text('ACTIVITY ${s.pathParameters['id']}'))),
    ]);

    tester.view.physicalSize = size;
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
    await tester.pump();
    await tester.pump();
    addTearDown(() => tester.pumpWidget(const SizedBox()));
  }

  Future<TestGesture> mouse(WidgetTester tester, Offset at) async {
    final gesture = await tester.createGesture(kind: PointerDeviceKind.mouse);
    await gesture.addPointer(location: Offset.zero);
    addTearDown(gesture.removePointer);
    await gesture.moveTo(at);
    await tester.pump();
    return gesture;
  }

  /// The hostnames in the order the rows are drawn, top to bottom.
  List<String> order(WidgetTester tester) {
    final names = ['alpha', 'bravo', 'charlie', 'delta'].where((n) => find.text(n).evaluate().isNotEmpty).toList();
    names.sort((a, b) => tester.getTopLeft(find.text(a)).dy.compareTo(tester.getTopLeft(find.text(b)).dy));
    return names;
  }

  group('the mapper', () {
    test('reads the new fields', () {
      final h = _hosts().first;

      expect(h.department, 'Technology');
      expect(h.location, 'Sydney');
      expect(h.model, 'Surface Laptop 7');
      expect(h.assignedShortName, 'Alex M.');
      expect(h.hasAssignedPerson, isTrue);
      expect(h.health, HostHealthState.problem);
      expect(h.healthReason, '2 failed patches');
      expect(h.failedPatchCount, 2);
    });

    test('an older server that sends none of them gives a plain healthy host', () {
      final h = hostFromJson({'id': 'x', 'hostname': 'old', 'serialNumber': 'S'});

      expect(h.department, isNull);
      expect(h.location, isNull);
      expect(h.model, isNull);
      expect(h.organization, isNull);
      expect(h.assignedShortName, isNull);
      expect(h.hasAssignedPerson, isFalse);
      expect(h.health, HostHealthState.healthy);
      expect(h.healthReason, isNull);
      expect(h.failedPatchCount, 0);
      expect(h.unstable, isFalse);
    });

    test('reads a blank string as nothing, and an unknown health as healthy', () {
      final h = hostFromJson({'id': 'x', 'hostname': 'h', 'department': '  ', 'model': '', 'health': 'Surprising'});

      expect(h.department, isNull);
      expect(h.model, isNull);
      expect(h.health, HostHealthState.healthy);
    });

    test('reads the answers of the reveal and import calls', () {
      expect(assignedPersonFromJson({'name': 'Alex Morgan', 'email': null}), const AssignedPerson(name: 'Alex Morgan'));
      expect(importResultFromJson({'matched': 3, 'notFound': 1, 'updated': 2}),
          const HostProfileImportResult(matched: 3, notFound: 1, updated: 2));
      expect(importResultFromJson({}), const HostProfileImportResult(matched: 0, notFound: 0, updated: 0));
    });

    test('the two new timeline kinds are read by name, with plain labels, and the old ordinals stay put', () {
      expect(hostEventKindFromJson('HostProfileChanged'), HostEventKind.hostProfileChanged);
      expect(hostEventKindFromJson('AssignedPersonRevealed'), HostEventKind.assignedPersonRevealed);
      expect(hostEventKindFromJson(13), HostEventKind.logsFailed);
      expect(hostEventKindFromJson(14), HostEventKind.hostProfileChanged);

      HostEvent event(HostEventKind kind) => HostEvent(
            id: 'e', occurredUtc: DateTime(2026), kind: kind, subject: 'x', fromVersion: null, toVersion: null,
            actor: 'admin@example.com', detail: null, count: 1,
          );
      expect(event(HostEventKind.hostProfileChanged).summary, 'Profile changed');
      expect(event(HostEventKind.assignedPersonRevealed).summary, 'Assigned person revealed');
      expect(HostEventKind.assignedPersonRevealed.group, HostEventGroup.adminActions);
    });
  });

  group('the order of the table', () {
    testWidgets('opens with connected hosts first, then the rest by most recent check-in, and its arrow is showing',
        (tester) async {
      await pump(tester);

      expect(order(tester), ['charlie', 'alpha', 'bravo', 'delta']);
      expect(find.byIcon(Icons.arrow_drop_up), findsOneWidget, reason: 'the Last uptime header carries the arrow');
      expect(find.text('Connected'), findsNWidgets(2));
      expect(find.text('3 days ago'), findsOneWidget);
      expect(find.text('a month ago'), findsOneWidget);
    });

    testWidgets('pressing a header sorts by it, and pressing it again reverses it', (tester) async {
      await pump(tester);

      await tester.tap(find.text('LOCATION'));
      await tester.pump();
      // Alphabetical by city, and the host with no location last.
      expect(order(tester), ['bravo', 'charlie', 'alpha', 'delta']);
      expect(find.byIcon(Icons.arrow_drop_up), findsOneWidget);

      await tester.tap(find.text('LOCATION'));
      await tester.pump();
      expect(order(tester), ['delta', 'alpha', 'charlie', 'bravo']);
      expect(find.byIcon(Icons.arrow_drop_down), findsOneWidget);
    });

    testWidgets('Health is worst first, and Department is alphabetical with none last', (tester) async {
      // The table is wider than the usual test screen; this one needs every header in view.
      await pump(tester, size: const Size(2500, 2600));

      await tester.tap(find.text('HEALTH'));
      await tester.pump();
      expect(order(tester), ['alpha', 'bravo', 'charlie', 'delta'], reason: 'Problem, Pending, then the healthy by name');

      await tester.tap(find.text('DEPARTMENT'));
      await tester.pump();
      expect(order(tester), ['bravo', 'charlie', 'alpha', 'delta'], reason: 'HR (by name), Technology, none last');
    });

    testWidgets('only one sort at a time: choosing another column moves the arrow', (tester) async {
      await pump(tester);

      await tester.tap(find.text('HOSTNAME'));
      await tester.pump();

      expect(order(tester), ['alpha', 'bravo', 'charlie', 'delta']);
      expect(find.byIcon(Icons.arrow_drop_up), findsOneWidget);
    });
  });

  group('the columns', () {
    testWidgets('are the mockup\'s twelve, and the removed ones are gone', (tester) async {
      await pump(tester);

      for (final header in ['HOSTNAME', 'SERIAL', 'MODEL', 'DEPARTMENT', 'LOCATION', 'HEALTH', 'UPDATES', 'CVES', 'STATUS',
          'LAST UPTIME', 'ACTIONS']) {
        expect(find.text(header), findsOneWidget, reason: header);
      }
      for (final gone in ['OS UPDATE', 'IP ADDRESS', 'FAILED', 'APP UPDATES', 'SERIAL NUMBER', 'CVES (PATCHABLE)', 'CVES (UNPATCHABLE)']) {
        expect(find.text(gone), findsNothing, reason: gone);
      }
    });

    testWidgets('all fit a 1,600px-wide table with no sideways scroll, and hostnames and serials stay on one line', (tester) async {
      // The table's own width is what matters (the page has its own margins), so measure it and widen
      // the screen until it is 1,600.
      await pump(tester, size: const Size(1750, 1400));
      final panel = tester.getRect(find.byType(KintsugiTable));
      expect(panel.width, greaterThanOrEqualTo(1590), reason: 'the test screen is too narrow to stand for a 1,600px table');

      // Every header, and the last column's three icons, are inside the table's right edge.
      for (final header in ['HOSTNAME', 'SERIAL', 'MODEL', 'DEPARTMENT', 'LOCATION', 'HEALTH', 'UPDATES', 'CVES', 'STATUS',
          'LAST UPTIME', 'ACTIONS']) {
        expect(tester.getRect(find.text(header)).right, lessThan(panel.right), reason: header);
      }
      for (final icon in find.byIcon(Icons.delete_outline).evaluate()) {
        expect(tester.getRect(find.byWidget(icon.widget)).right, lessThanOrEqualTo(panel.right));
      }

      // One line each: no taller than a line of the table's small type.
      expect(tester.getSize(find.text('A1')).height, lessThan(18));
      expect(tester.getSize(find.text('alpha')).height, lessThan(20));
    });

    testWidgets('CVEs is one column: the badge is the count not confirmed fixed, the tooltip has the other', (tester) async {
      await pump(tester);

      final badge = find.text('5');
      expect(badge, findsOneWidget);
      final tip = tester.widget<Tooltip>(find.ancestor(of: badge, matching: find.byType(Tooltip)).first);
      expect(tip.message, contains('not confirmed fixed'));
      expect(tip.message, contains('3 confirmed already up to date'));
      // It is the alert badge, as the old column was.
      expect(find.text('7'), findsOneWidget, reason: 'the Updates badge');
    });

    testWidgets('an unstable host is a grey badge under a Healthy chip, not a red one', (tester) async {
      await pump(tester);

      expect(find.byKey(const Key('unstable-badge')), findsOneWidget);
      expect(find.text('UNSTABLE'), findsOneWidget);
      // charlie is the unstable one and is still Healthy, so the Problem tile counts only alpha.
      expect(tester.widget<Text>(find.byKey(const Key('tile-Problem'))).data, '1');
    });

    testWidgets('"2 failed patches" opens Failed Updates for that host, and a host with none has no link', (tester) async {
      await pump(tester);

      expect(find.byKey(const Key('failed-patches-link')), findsOneWidget, reason: 'only alpha has failed patches');
      await tester.tap(find.byKey(const Key('failed-patches-link')));
      await tester.pumpAndSettle();

      expect(find.text('FAILED alpha'), findsOneWidget);
    });

    testWidgets('sorting by CVEs and by Updates puts the highest first', (tester) async {
      await pump(tester);

      await tester.tap(find.text('CVES'));
      await tester.pump();
      expect(order(tester).first, 'alpha');

      await tester.tap(find.text('UPDATES'));
      await tester.pump();
      expect(order(tester).first, 'alpha');
    });

    testWidgets('the hover card shows the IP address, and the table does not', (tester) async {
      await pump(tester);
      expect(find.text('10.0.0.5'), findsNothing);

      await mouse(tester, tester.getCenter(find.text('alpha')));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.descendant(of: find.byKey(const Key('host-card-a')), matching: find.text('10.0.0.5')), findsOneWidget);
    });

    testWidgets('the details panel has what the table no longer does: OS update, IP, agent version, failed count', (tester) async {
      await pump(tester);
      expect(find.text('UP TO DATE'), findsNothing);

      await tester.tap(find.text('alpha'));
      await tester.pumpAndSettle();

      Finder row(String label) => find.ancestor(of: find.text(label), matching: find.byType(Row)).first;
      expect(find.text('OS update'), findsOneWidget);
      expect(find.text('UP TO DATE'), findsOneWidget);
      expect(find.descendant(of: row('IP address'), matching: find.text('10.0.0.5')), findsOneWidget);
      expect(find.descendant(of: row('Agent version'), matching: find.text('0.12.5')), findsOneWidget);
      expect(find.descendant(of: row('Failed patches'), matching: find.text('2')), findsOneWidget);
      expect(find.descendant(of: row('CVEs not fixed'), matching: find.text('5')), findsOneWidget);
      expect(find.descendant(of: row('CVEs up to date'), matching: find.text('3')), findsOneWidget);
    });
  });

  group('the tiles and the bars', () {
    testWidgets('the tiles count the hosts in each state', (tester) async {
      await pump(tester);

      String tile(String label) => (tester.widget<Text>(find.byKey(Key('tile-$label')))).data!;
      expect(tile('Enrolled in Kintsugi'), '4');
      expect(tile('Healthy'), '2');
      expect(tile('Pending'), '1');
      expect(tile('Problem'), '1');
      expect(tile('No department or location'), '1');
    });

    testWidgets('a bar has a coloured part for each state a group has, and none for a state it has not', (tester) async {
      await pump(tester);

      expect(find.byKey(const Key('segment-HR-pending')), findsOneWidget);
      expect(find.byKey(const Key('segment-HR-healthy')), findsOneWidget);
      expect(find.byKey(const Key('segment-HR-problem')), findsNothing);
      expect(find.byKey(const Key('segment-Technology-problem')), findsOneWidget);
    });

    testWidgets('hovering a coloured part says how many hosts it is', (tester) async {
      await pump(tester);

      await mouse(tester, tester.getCenter(find.byKey(const Key('segment-HR-pending'))));
      await tester.pump(const Duration(milliseconds: 250));

      expect(find.text('HR · Pending: 1 of 2 hosts (50%)'), findsOneWidget);
    });

    testWidgets('can be grouped by location instead', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('group-by-location')));
      await tester.pump();

      expect(find.byKey(const Key('group-name-Sydney')), findsOneWidget);
      expect(find.byKey(const Key('group-name-Perth')), findsOneWidget);
      expect(find.byKey(const Key('group-name-Technology')), findsNothing);
    });
  });

  group('a click on a coloured part of a bar', () {
    testWidgets('applies the group and the health state as two chips, and lists exactly those hosts', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('segment-HR-pending')));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));

      expect(find.text('Department: HR'), findsOneWidget);
      expect(find.text('Health: Pending'), findsOneWidget);
      expect(find.byKey(const Key('chip-department')), findsOneWidget);
      expect(find.byKey(const Key('chip-health')), findsOneWidget);
      expect(find.text('HR · PENDING'), findsOneWidget, reason: 'the line above the table says which hosts these are');
      expect(order(tester), ['bravo']);
      expect(find.text('1 of 4 host(s) match the filters'), findsOneWidget);
    });

    testWidgets('brings the table into view', (tester) async {
      await pump(tester, size: const Size(1700, 480));
      final before = tester.getTopLeft(find.byKey(const Key('table-title'))).dy;
      expect(before, greaterThan(480), reason: 'the table starts below the first screenful');

      await tester.tap(find.byKey(const Key('segment-HR-pending')), warnIfMissed: false);
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 400));
      await tester.pump(const Duration(milliseconds: 400));

      final after = tester.getTopLeft(find.byKey(const Key('table-title'))).dy;
      expect(after, lessThan(480));
      expect(after, greaterThanOrEqualTo(0));
    });

    testWidgets('clicking the same part again removes both chips', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('segment-HR-pending')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('segment-HR-pending')));
      await tester.pump();

      expect(find.byKey(const Key('chip-department')), findsNothing);
      expect(find.byKey(const Key('chip-health')), findsNothing);
      expect(order(tester), ['charlie', 'alpha', 'bravo', 'delta']);
      expect(find.text('ALL HOSTS'), findsOneWidget);
    });

    testWidgets('clicking the group\'s name filters by the group alone', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('group-name-HR')));
      await tester.pump();

      expect(find.byKey(const Key('chip-department')), findsOneWidget);
      expect(find.byKey(const Key('chip-health')), findsNothing);
      expect(order(tester), ['charlie', 'bravo']);

      // And the bars keep showing the group, so another name can be added to the choice.
      await tester.tap(find.byKey(const Key('group-name-Technology')));
      await tester.pump();
      expect(order(tester), ['charlie', 'alpha', 'bravo']);
      expect(find.text('Department: HR, Technology'), findsOneWidget);
    });

    testWidgets('the bars keep their context while a part is applied', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('segment-HR-pending')));
      await tester.pump();

      // Still both of HR's colours, not just the part that was chosen.
      expect(find.byKey(const Key('segment-HR-healthy')), findsOneWidget);
      expect(find.byKey(const Key('segment-HR-pending')), findsOneWidget);
    });
  });

  group('the filters the person chooses', () {
    testWidgets('+ Add filter lists the fields, then the values with counts, and applies as a chip', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('add-filter')));
      await tester.pumpAndSettle();
      for (final field in ['connection', 'updates', 'cves', 'departmentSet', 'locationSet', 'organization', 'department',
          'location', 'system', 'model', 'status', 'health']) {
        expect(find.byKey(Key('filter-field-$field')), findsOneWidget, reason: field);
      }

      await tester.tap(find.byKey(const Key('filter-field-connection')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('filter-value-Online')), findsOneWidget);
      expect(find.byKey(const Key('filter-value-Not online')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('filter-value-Online')), matching: find.text('2')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('filter-value-Not online')), matching: find.text('2')), findsOneWidget);

      await tester.tap(find.byKey(const Key('filter-value-Not online')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('filter-apply')));
      await tester.pumpAndSettle();

      expect(find.text('Connection: Not online'), findsOneWidget);
      expect(order(tester), ['bravo', 'delta']);
      expect(find.text('2 of 4 host(s) match the filters'), findsOneWidget);
    });

    testWidgets('a two-option field offers exactly its two values', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('add-filter')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('filter-field-departmentSet')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('filter-value-Set')), findsOneWidget);
      expect(find.byKey(const Key('filter-value-Not set')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('filter-value-Not set')), matching: find.text('1')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('filter-value-Set')), matching: find.text('3')), findsOneWidget);
    });

    testWidgets('a chip can be edited and removed, and Clear all removes the rest', (tester) async {
      await pump(tester);
      Future<void> choose(String field, String value) async {
        await tester.tap(find.byKey(const Key('add-filter')));
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(Key('filter-field-$field')));
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(Key('filter-value-$value')));
        await tester.pump();
        await tester.tap(find.byKey(const Key('filter-apply')));
        await tester.pumpAndSettle();
      }

      await choose('department', 'HR');
      await choose('connection', 'Online');
      // OR inside a field, AND across fields: HR and online is only charlie.
      expect(order(tester), ['charlie']);

      // Edit the department chip: add Technology to HR.
      await tester.tap(find.byKey(const Key('chip-edit-department')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('filter-value-Technology')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('filter-apply')));
      await tester.pumpAndSettle();
      expect(find.text('Department: HR, Technology'), findsOneWidget);
      expect(order(tester), ['charlie', 'alpha']);

      await tester.tap(find.byKey(const Key('chip-remove-connection')));
      await tester.pump();
      expect(find.byKey(const Key('chip-connection')), findsNothing);
      expect(order(tester), ['charlie', 'alpha', 'bravo']);

      await tester.tap(find.text('Clear all'));
      await tester.pump();
      expect(find.byKey(const Key('chip-department')), findsNothing);
      expect(order(tester), ['charlie', 'alpha', 'bravo', 'delta']);
    });

    testWidgets('there are no header filter menus: the header arrows only sort', (tester) async {
      await pump(tester);

      await tester.tap(find.text('DEPARTMENT'));
      await tester.pump();

      expect(find.byKey(const Key('filter-value-Set')), findsNothing);
      expect(find.byKey(const Key('chip-departmentSet')), findsNothing);
    });
  });

  group('a host with no model', () {
    testWidgets('has a blank model: its system alone in the cell, with no dash and no placeholder', (tester) async {
      await pump(tester);

      final charlie = find.byKey(const Key('model-c'));
      expect(find.descendant(of: charlie, matching: find.text('macOS 15')), findsOneWidget);
      expect(find.descendant(of: charlie, matching: find.text('—')), findsNothing);
      expect(find.descendant(of: charlie, matching: find.textContaining('not set', findRichText: true)), findsNothing);
      expect(find.descendant(of: charlie, matching: find.textContaining('Not set')), findsNothing);

      // A host that has one shows it, with the system beneath.
      final alpha = find.byKey(const Key('model-a'));
      expect(find.descendant(of: alpha, matching: find.text('Surface Laptop 7')), findsOneWidget);
      expect(find.descendant(of: alpha, matching: find.text('Windows 11 Pro')), findsOneWidget);
      final model = tester.getTopLeft(find.descendant(of: alpha, matching: find.text('Surface Laptop 7'))).dy;
      final system = tester.getTopLeft(find.descendant(of: alpha, matching: find.text('Windows 11 Pro'))).dy;
      expect(system, greaterThan(model));
    });

    testWidgets('is not a group of the Model bars, and not a value of the Model filter', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('group-by-model')));
      await tester.pump();
      expect(find.byKey(const Key('group-name-Surface Laptop 7')), findsOneWidget);
      expect(find.byKey(const Key('group-name-Not set')), findsNothing);

      await tester.tap(find.byKey(const Key('add-filter')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('filter-field-model')));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('filter-value-Surface Laptop 7')), findsOneWidget);
      expect(find.byKey(const Key('filter-value-Not set')), findsNothing);
    });

    testWidgets('has no Model row on the hover card or in the details panel', (tester) async {
      await pump(tester);

      await mouse(tester, tester.getCenter(find.text('charlie')));
      await tester.pump(const Duration(milliseconds: 50));
      expect(find.byKey(const Key('host-card-c')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('host-card-c')), matching: find.text('Model')), findsNothing);
    });
  });

  group('the hostname', () {
    testWidgets('shows a card on hover with copy icons for the hostname and the serial', (tester) async {
      await pump(tester);

      await mouse(tester, tester.getCenter(find.text('alpha')));
      await tester.pump(const Duration(milliseconds: 50));

      final card = find.byKey(const Key('host-card-a'));
      expect(card, findsOneWidget);
      expect(find.descendant(of: card, matching: find.text('A1')), findsOneWidget);
      expect(find.descendant(of: card, matching: find.text('Alex M.')), findsOneWidget);
      expect(find.descendant(of: card, matching: find.text('Technology')), findsOneWidget);
      expect(find.descendant(of: card, matching: find.text('Sydney')), findsOneWidget);
      expect(find.byKey(const Key('card-copy-hostname-a')), findsOneWidget);
      expect(find.byKey(const Key('card-copy-serial-a')), findsOneWidget);

      await tester.tap(find.byKey(const Key('card-copy-serial-a')));
      await tester.pump();
      expect(clipboard, ['A1']);
      expect(find.text('Copied A1'), findsOneWidget);
    });

    testWidgets('says "Not set yet" for what a host has no value for, and has no copy icon for the person', (tester) async {
      await pump(tester);

      await mouse(tester, tester.getCenter(find.text('delta')));
      await tester.pump(const Duration(milliseconds: 50));

      final card = find.byKey(const Key('host-card-d'));
      expect(find.descendant(of: card, matching: find.text('Not set yet')), findsNWidgets(3), reason: 'person, department, location');
      // Only the hostname and the serial can be copied from the card.
      expect(find.descendant(of: card, matching: find.byIcon(Icons.copy_outlined)), findsNWidgets(2));
    });

    testWidgets('keeps the card open while the mouse is on it, and closes it a moment after the mouse leaves', (tester) async {
      await pump(tester);
      final gesture = await mouse(tester, tester.getCenter(find.text('alpha')));
      await tester.pump(const Duration(milliseconds: 50));
      expect(find.byKey(const Key('host-card-a')), findsOneWidget);

      await gesture.moveTo(tester.getCenter(find.byKey(const Key('host-card-a'))));
      await tester.pump(const Duration(milliseconds: 400));
      expect(find.byKey(const Key('host-card-a')), findsOneWidget, reason: 'the mouse is on the card');

      await gesture.moveTo(const Offset(5, 5));
      await tester.pump(const Duration(milliseconds: 400));
      expect(find.byKey(const Key('host-card-a')), findsNothing);
    });

    testWidgets('shows a copy icon next to the hostname when the row is hovered, and it copies the name', (tester) async {
      await pump(tester);
      expect(find.byKey(const Key('copy-hostname-a')), findsNothing);

      await mouse(tester, tester.getCenter(find.text('A1')));
      await tester.pump(const Duration(milliseconds: 50));

      expect(find.byKey(const Key('copy-hostname-a')), findsOneWidget);
      await tester.tap(find.byKey(const Key('copy-hostname-a')));
      await tester.pump();
      expect(clipboard, ['alpha']);
    });
  });

  group('the details panel', () {
    Future<void> open(WidgetTester tester, String hostname) async {
      await tester.tap(find.text(hostname));
      await tester.pumpAndSettle();
    }

    testWidgets('shows only the short name and a hidden email until Reveal is pressed, and asks the server nothing', (tester) async {
      await pump(tester);

      await open(tester, 'alpha');

      expect(find.text('ADMINS ONLY'), findsOneWidget);
      expect(find.text('Alex M.'), findsOneWidget);
      expect(find.text('••••••@••••••'), findsOneWidget);
      expect(find.text('Alex Morgan'), findsNothing);
      expect(find.text('alex.morgan@example.com'), findsNothing);
      expect(find.byKey(const Key('details-copy-name')), findsNothing, reason: 'nothing to copy until it is revealed');
      expect(repo.revealCalls, isEmpty, reason: 'opening the panel must not ask for the person');
    });

    testWidgets('Reveal asks once, shows the full name and email with copy icons, and says it was recorded', (tester) async {
      await pump(tester);
      await open(tester, 'alpha');

      await tester.tap(find.byKey(const Key('reveal-person')));
      await tester.pumpAndSettle();

      expect(repo.revealCalls, ['a']);
      expect(find.text('Alex Morgan'), findsOneWidget);
      expect(find.text('alex.morgan@example.com'), findsOneWidget);
      expect(find.text('Alex M.'), findsNothing);
      expect(find.byKey(const Key('reveal-person')), findsNothing, reason: 'it cannot be pressed twice');
      expect(find.textContaining(RegExp(r"Revealed at \d\d:\d\d\. Recorded on this host's activity timeline with your name\.")), findsOneWidget);

      await tester.tap(find.byKey(const Key('details-copy-email')));
      await tester.pump();
      expect(clipboard, ['alex.morgan@example.com']);
      await tester.tap(find.byKey(const Key('details-copy-name')));
      await tester.pump();
      expect(clipboard.last, 'Alex Morgan');
      expect(repo.revealCalls, ['a'], reason: 'copying does not ask again');
    });

    testWidgets('a failed reveal says so and can be tried again', (tester) async {
      repo.revealError = const ApiException('The server is busy.');
      await pump(tester);
      await open(tester, 'alpha');

      await tester.tap(find.byKey(const Key('reveal-person')));
      await tester.pumpAndSettle();

      expect(find.text('Could not reveal: The server is busy.'), findsOneWidget);
      expect(find.byKey(const Key('reveal-person')), findsOneWidget);
      expect(find.text('Alex Morgan'), findsNothing);
    });

    testWidgets('Remove name and email asks first, then clears the person and offers no Reveal', (tester) async {
      await pump(tester);
      await open(tester, 'alpha');

      await tester.tap(find.byKey(const Key('clear-person')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('clear-person-cancel')));
      await tester.pumpAndSettle();
      expect(repo.clearCalls, isEmpty, reason: 'cancelling removes nothing');
      expect(find.text('Alex M.'), findsOneWidget);

      await tester.tap(find.byKey(const Key('clear-person')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('clear-person-confirm')));
      await tester.pumpAndSettle();

      expect(repo.clearCalls, ['a']);
      expect(find.text('Alex M.'), findsNothing);
      expect(find.byKey(const Key('reveal-person')), findsNothing);
      expect(find.textContaining('The name and email were removed'), findsOneWidget);
    });

    testWidgets('Start maintenance asks how long and why, sends it, and then offers End maintenance', (tester) async {
      await pump(tester);
      await open(tester, 'charlie');

      await tester.ensureVisible(find.byKey(const Key('maintenance-start')));
      await tester.tap(find.byKey(const Key('maintenance-start')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('maintenance-cancel')));
      await tester.pumpAndSettle();
      expect(repo.maintenanceCalls, isEmpty, reason: 'cancelling starts nothing');

      await tester.tap(find.byKey(const Key('maintenance-start')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('maintenance-duration-4')));
      await tester.enterText(find.byKey(const Key('maintenance-reason')), 'disk swap');
      await tester.tap(find.byKey(const Key('maintenance-confirm')));
      await tester.pumpAndSettle();

      expect(repo.maintenanceCalls, ['start c timed disk swap']);
      expect(find.byKey(const Key('maintenance-start')), findsNothing);
      expect(find.textContaining('keeps checking in'), findsOneWidget);

      await tester.ensureVisible(find.byKey(const Key('maintenance-end')));
      await tester.tap(find.byKey(const Key('maintenance-end')));
      await tester.pumpAndSettle();
      expect(repo.maintenanceCalls.last, 'end c');
      expect(find.byKey(const Key('maintenance-start')), findsOneWidget);
    });

    testWidgets('a host a server says is in maintenance opens with End maintenance and its end time', (tester) async {
      repo.hosts = [
        hostFromJson({
          ..._json('m', 'mike', status: 0, seen: const Duration(minutes: 1)),
          'inMaintenance': true,
          'maintenanceUntilUtc': DateTime.now().toUtc().add(const Duration(hours: 3)).toIso8601String(),
          'maintenanceReason': 'disk swap',
        }),
      ];
      await pump(tester);
      await open(tester, 'mike');

      expect(find.byKey(const Key('maintenance-end')), findsOneWidget);
      expect(find.byKey(const Key('maintenance-start')), findsNothing);
      expect(find.text('disk swap'), findsOneWidget);
    });

    testWidgets('a host with nobody assigned says so, and offers no Reveal', (tester) async {
      await pump(tester);

      await open(tester, 'charlie');

      expect(find.byKey(const Key('reveal-person')), findsNothing);
      expect(find.textContaining('No name or email has been imported'), findsOneWidget);
    });

    testWidgets('has an Actions menu: Open terminal, Connect and, apart, Remove host', (tester) async {
      await pump(tester);
      await open(tester, 'alpha');

      await tester.tap(find.byKey(const Key('details-actions')));
      await tester.pumpAndSettle();

      expect(find.text('Open terminal'), findsOneWidget);
      expect(find.text('Connect'), findsOneWidget);
      expect(find.text('Remove host'), findsOneWidget);

      await tester.tap(find.text('Open terminal'));
      await tester.pumpAndSettle();
      expect(find.text('SHELL a alpha'), findsOneWidget);
    });

    testWidgets('Connect opens remote control, and Open host page opens the activity page', (tester) async {
      await pump(tester);
      await open(tester, 'bravo');

      await tester.tap(find.byKey(const Key('details-actions')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Connect'));
      await tester.pumpAndSettle();
      expect(find.text('REMOTE b bravo'), findsOneWidget);
    });

    testWidgets('Open host page goes to the host\'s activity page', (tester) async {
      await pump(tester);
      await open(tester, 'bravo');

      await tester.tap(find.byKey(const Key('details-host-page')));
      await tester.pumpAndSettle();

      expect(find.text('ACTIVITY b'), findsOneWidget);
    });

    testWidgets('Remove host asks to confirm first', (tester) async {
      await pump(tester);
      await open(tester, 'alpha');

      await tester.tap(find.byKey(const Key('details-actions')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Remove host'));
      await tester.pumpAndSettle();

      expect(find.text('Remove alpha?'), findsOneWidget);
    });

    testWidgets('closes with its X, back to the table', (tester) async {
      await pump(tester);
      await open(tester, 'alpha');

      await tester.tap(find.byKey(const Key('details-close')));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('details-actions')), findsNothing);
    });
  });

  group('the bulk edit', () {
    testWidgets('ticks hosts, sets a department for them and says how many were updated', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('select-a')));
      await tester.tap(find.byKey(const Key('select-c')));
      await tester.pump();
      expect(find.text('2 selected'), findsOneWidget);

      await tester.tap(find.byKey(const Key('bulk-edit')));
      await tester.pumpAndSettle();
      expect(find.text('Applies to 2 selected hosts. A field left empty keeps what each host has.'), findsOneWidget);
      // The values already in use are offered.
      expect(find.widgetWithText(ActionChip, 'Technology'), findsOneWidget);
      expect(find.widgetWithText(ActionChip, 'Sydney'), findsOneWidget);

      await tester.enterText(find.byKey(const Key('bulk-department')), 'Finance');
      await tester.pump();
      await tester.tap(find.byKey(const Key('bulk-apply')));
      await tester.pumpAndSettle();

      expect(repo.profileUpdates, hasLength(1));
      expect(repo.profileUpdates.single.ids.toSet(), {'a', 'c'});
      expect(repo.profileUpdates.single.department, 'Finance');
      expect(repo.profileUpdates.single.location, isNull, reason: 'left empty keeps it');
      expect(find.text('Updated 2 hosts.'), findsOneWidget);
      expect(find.byKey(const Key('bulk-bar')), findsNothing, reason: 'the selection is cleared');
    });

    testWidgets('a suggestion fills the field, and Apply stays disabled while both fields are empty', (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('select-b')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('bulk-edit')));
      await tester.pumpAndSettle();

      applyButton() => tester.widget<FilledButton>(find.descendant(of: find.byKey(const Key('bulk-apply')), matching: find.byType(FilledButton)));
      expect(applyButton().onPressed, isNull);

      await tester.tap(find.widgetWithText(ActionChip, 'Perth'));
      await tester.pump();
      expect(applyButton().onPressed, isNotNull);
      await tester.tap(find.byKey(const Key('bulk-apply')));
      await tester.pumpAndSettle();

      expect(repo.profileUpdates.single.location, 'Perth');
      expect(repo.profileUpdates.single.department, isNull);
    });

    testWidgets('can clear a value instead of setting one', (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('select-b')));
      await tester.pump();
      await tester.tap(find.byKey(const Key('bulk-edit')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Clear the department instead'));
      await tester.pump();
      await tester.tap(find.byKey(const Key('bulk-apply')));
      await tester.pumpAndSettle();

      expect(repo.profileUpdates.single.department, '', reason: 'an empty string clears it on the server');
    });

    testWidgets('the header box ticks every host the filters leave', (tester) async {
      await pump(tester);

      await tester.tap(find.byKey(const Key('select-all')));
      await tester.pump();
      expect(find.text('4 selected'), findsOneWidget);

      await tester.tap(find.text('Clear selection'));
      await tester.pump();
      expect(find.byKey(const Key('bulk-bar')), findsNothing);
    });
  });

  group('the import', () {
    Future<void> pick(WidgetTester tester) async {
      await tester.tap(find.byKey(const Key('import-open')));
      await tester.pumpAndSettle();
      await tester.tap(find.byKey(const Key('import-pick')));
      await tester.pumpAndSettle();
    }

    testWidgets('reads the file in the browser and sends only the columns chosen: not the name, not the email', (tester) async {
      await pump(tester);

      await pick(tester);
      expect(find.text('assets.csv'), findsOneWidget);
      expect(find.byKey(const Key('import-found')), findsOneWidget);
      expect(find.textContaining('3 rows with a serial number.'), findsOneWidget);
      expect(find.text('Will send: serial number, department, location, model.'), findsOneWidget);
      expect(repo.imports, isEmpty, reason: 'choosing the file sends nothing');

      await tester.tap(find.byKey(const Key('import-apply')));
      await tester.pumpAndSettle();

      expect(repo.imports, hasLength(1));
      final rows = repo.imports.single;
      expect(rows.map((r) => r.serialNumber), ['S-ONE', 'S-TWO', 'S-THREE']);
      expect(rows.first.department, 'Technology');
      expect(rows.first.location, 'Adelaide');
      expect(rows.first.model, 'Surface Laptop 7');
      expect(rows.every((r) => r.assignedName == null && r.assignedEmail == null), isTrue);
      expect(find.text('Matched by serial 2 of 3, 1 not found. 2 hosts updated.'), findsOneWidget);
    });

    testWidgets('both boxes start unticked, and ticking one adds only that column', (tester) async {
      await pump(tester);
      await pick(tester);

      Checkbox box(String label) => tester.widget<Checkbox>(find.descendant(
            of: find.ancestor(of: find.text(label), matching: find.byType(InkWell)).first,
            matching: find.byType(Checkbox),
          ));
      expect(box("Also import the assigned person's name").value, isFalse);
      expect(box('Also import their email').value, isFalse);

      await tester.tap(find.text("Also import the assigned person's name"));
      await tester.pump();
      expect(find.text("Will send: serial number, department, location, model, the assigned person's name."), findsOneWidget);

      await tester.tap(find.byKey(const Key('import-apply')));
      await tester.pumpAndSettle();

      final rows = repo.imports.single;
      expect(rows.first.assignedName, 'Alex Morgan');
      expect(rows.every((r) => r.assignedEmail == null), isTrue, reason: 'the email box was not ticked');
    });

    testWidgets('a Users export has no serial column, and the dialog says what to export instead', (tester) async {
      await pump(tester, csv: 'Username,Name,Email,Department,Location,Assets\r\nu1,A B,a@example.com,HR,Perth,1\r\n');

      await pick(tester);

      expect(find.byKey(const Key('import-no-serial')), findsOneWidget);
      expect(find.textContaining('a Users export has no serial numbers'), findsOneWidget);
      final apply = tester.widget<FilledButton>(find.descendant(of: find.byKey(const Key('import-apply')), matching: find.byType(FilledButton)));
      expect(apply.onPressed, isNull);
      expect(repo.imports, isEmpty);
    });

    testWidgets('closing after an import says so on the page', (tester) async {
      await pump(tester);
      await pick(tester);
      await tester.tap(find.byKey(const Key('import-apply')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('Close'));
      await tester.pumpAndSettle();

      expect(find.text('Imported: matched by serial 2 of 3, 1 not found, 2 hosts updated.'), findsOneWidget);
    });
  });
}
