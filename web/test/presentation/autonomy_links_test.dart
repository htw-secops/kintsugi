import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/autonomy_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/vulnerability.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_cve.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_model.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_rings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/domain/usecases/vulnerability_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_bloc.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_filters.dart';
import 'package:kintsugi_web/presentation/vulnerabilities/vulnerabilities_bloc.dart';

VulnerabilityFinding _cve(String id, List<String> apps) => VulnerabilityFinding(
      cveId: id,
      description: null,
      cvssBaseScore: 9,
      cvssSeverity: null,
      cvssVector: null,
      cvssVersion: null,
      cvssDerivedFromVector: false,
      publishedUtc: null,
      knownExploited: false,
      kevDateAddedUtc: null,
      kevDueDateUtc: null,
      kevKnownRansomwareUse: false,
      kevShortDescription: null,
      kevRequiredAction: null,
      hostCount: 1,
      affectedSubjects: [
        for (final a in apps)
          AffectedSubject(cpeMappingId: 'm', subjectKind: VulnerabilitySubjectKind.application, displayName: a, version: '1', hostCount: 1),
      ],
      platforms: const ['macOS'],
    );

Map<String, dynamic> _autonomy() => {
      'period': '30d',
      'fromUtc': '2026-10-08T00:00:00Z',
      'toUtc': '2026-11-06T12:00:00Z',
      'month': null,
      'historyStartsUtc': null,
      'hostsWithEvents': 0,
      'totalHosts': 10,
      'devicesDelayed': 2,
      'totalDeviceDays': 4.0,
      'patches': {'total': 0, 'ownSchedule': 0, 'adminStarted': 0},
      'buckets': [],
      'months': [],
      'causes': [
        {
          'key': 'disconnected',
          'title': 'Disconnected',
          'category': 'Disconnected',
          'measured': true,
          'devices': 2,
          'deviceDays': 4.0,
          'sharePercent': 100,
          'explanation': null,
          'suggestedFix': null,
          'patchNowConnected': 0,
          'patchNowOffline': 2,
        },
      ],
      'kpis': [],
      'events': [],
    };

class _Hosts extends Fake implements HostRepository {}

class _Vulns extends Fake implements VulnerabilityRepository {
  final asked = <VulnerabilityQuery>[];

  @override
  Future<VulnerabilityOverview> readOverview(VulnerabilityQuery query) async {
    asked.add(query);
    throw const ApiException('stop');
  }
}

void main() {
  group('the screens a link opens', () {
    test('the Hosts bloc starts with the filters it is given', () async {
      final repo = _Hosts();
      final bloc = HostsBloc(
        getHosts: GetHosts(repo),
        requestHostRemoval: RequestHostRemoval(repo),
        updateHostProfiles: UpdateHostProfiles(repo),
        initialFilters: hostFiltersFromQuery({'health': 'Pending,Problem'}),
      );
      addTearDown(bloc.close);
      expect(bloc.state.filters.valuesOf(HostFilterField.health), {'Pending', 'Problem'});
    });

    test('the CVE list loads with the query it is given, not the default', () async {
      final repo = _Vulns();
      final bloc = VulnerabilitiesBloc(
        getOverview: GetVulnerabilityOverview(repo),
        initialQuery: const VulnerabilityQuery(knownExploitedOnly: false, cveSearch: 'CVE-2026-1'),
      );
      addTearDown(bloc.close);
      bloc.add(const VulnerabilitiesRequested());
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(repo.asked.single.knownExploitedOnly, isFalse);
      expect(repo.asked.single.cveSearch, 'CVE-2026-1');
    });
  });

  group('the links', () {
    test('Hosts opens with the Health filter a link names, and ignores anything else', () {
      expect(hostFiltersFromQuery({'health': 'Pending,Problem'}).valuesOf(HostFilterField.health), {'Pending', 'Problem'});
      expect(hostFiltersFromQuery({'health': 'Healthy'}).valuesOf(HostFilterField.health), {'Healthy'});
      expect(hostFiltersFromQuery({'health': 'Nonsense,Pending'}).valuesOf(HostFilterField.health), {'Pending'});
      expect(hostFiltersFromQuery({'health': 'Nonsense'}).isEmpty, isTrue);
      expect(hostFiltersFromQuery({}).isEmpty, isTrue);
    });

    test('the addresses the rings build', () {
      expect(Routes.hostsWithHealth(['Pending', 'Problem']), '/hosts?health=Pending%2CProblem');
      expect(Routes.vulnerabilitiesFor(cve: 'CVE-2026-1'), '/vulnerabilities?exploited=0&cve=CVE-2026-1');
      expect(Routes.vulnerabilitiesFor(), '/vulnerabilities?exploited=0');
      // The address round-trips into the filters.
      final q = Uri.parse(Routes.hostsWithHealth(['Pending', 'Problem'])).queryParameters;
      expect(hostFiltersFromQuery(q).valuesOf(HostFilterField.health), {'Pending', 'Problem'});
    });

    test('every ring slice says where it leads', () {
      final rings = ringsFor(autonomyFromJson(_autonomy()));
      final patching = rings[0].slices;
      expect(patching.map((s) => s.route), [Routes.hostsWithHealth(['Healthy']), Routes.hostsWithHealth(['Pending']), Routes.hostsWithHealth(['Problem'])]);
      final health = rings[1].slices;
      expect(health[0].clickable, isFalse); // Healthy leads nowhere better than "everything"
      expect(health[1].anchor, AutonomyAnchor.deviceHealth);
      expect(health[2].anchor, AutonomyAnchor.deviceHealth);
      final cost = rings[2].slices.first;
      expect(cost.anchor, AutonomyAnchor.causes);
      expect(cost.causeKey, 'disconnected');

      final cve = cveRing([_cve('CVE-2026-1', ['A', 'B']), _cve('CVE-2026-2', ['A'])]);
      expect(cve.slices.map((s) => s.route), [Routes.vulnerabilitiesFor(cve: 'CVE-2026-1'), Routes.vulnerabilitiesFor(cve: 'CVE-2026-2')]);
    });
  });

  group('sliceAt', () {
    const size = Size(280, 280);
    final slices = [
      const RingSlice('a', 25, RingKind.good), // 12 to 3 o'clock
      const RingSlice('b', 25, RingKind.waiting), // 3 to 6
      const RingSlice('c', 50, RingKind.bad), // 6 to 12
    ];
    final c = size.center(Offset.zero);

    test('goes clockwise from twelve o\'clock', () {
      expect(sliceAt(c + const Offset(70, -70), size, slices)?.label, 'a');
      expect(sliceAt(c + const Offset(70, 70), size, slices)?.label, 'b');
      expect(sliceAt(c + const Offset(-70, 70), size, slices)?.label, 'c');
      expect(sliceAt(c + const Offset(-70, -70), size, slices)?.label, 'c');
    });

    test('only the band counts: not the middle, not the corners', () {
      expect(sliceAt(c, size, slices), isNull);
      expect(sliceAt(c + const Offset(30, 0), size, slices), isNull);
      expect(sliceAt(Offset.zero, size, slices), isNull);
      expect(sliceAt(c + const Offset(101, 0), size, slices)?.label, 'a');
    });

    test('an empty ring has nothing to hit', () {
      expect(sliceAt(c + const Offset(101, 0), size, const []), isNull);
    });
  });

  group('clicking', () {
    late List<String> opened;
    late List<(AutonomyAnchor, String?)> anchors;

    Future<void> pump(WidgetTester tester, {List<VulnerabilityFinding>? cves}) async {
      opened = [];
      anchors = [];
      tester.view.physicalSize = const Size(1500, 1400);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final router = GoRouter(routes: [
        GoRoute(
          path: '/',
          builder: (_, _) => Scaffold(
            body: SingleChildScrollView(
              child: AutonomyRings(
                data: autonomyFromJson(_autonomy()),
                findings: cves,
                now: DateTime.utc(2026, 11, 26),
                onAnchor: (a, k) => anchors.add((a, k)),
              ),
            ),
          ),
        ),
        GoRoute(
          path: '/hosts',
          builder: (_, state) {
            opened.add(state.uri.toString());
            return const Scaffold(body: Text('hosts page'));
          },
        ),
        GoRoute(
          path: '/vulnerabilities',
          builder: (_, state) {
            opened.add(state.uri.toString());
            return const Scaffold(body: Text('cve page'));
          },
        ),
      ]);
      await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
      await tester.pumpAndSettle();
    }

    Offset ringCentre(WidgetTester tester, String title) =>
        tester.getCenter(find.descendant(of: find.byKey(Key('ring-$title')), matching: find.byType(CustomPaint)).first);

    testWidgets('a legend row opens the Hosts page filtered to that health', (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('legend-Waiting (offline, restart)')));
      await tester.pumpAndSettle();
      expect(opened, ['/hosts?health=Pending']);
    });

    testWidgets('a click on the coloured band does the same, and one in the middle does nothing', (tester) async {
      await pump(tester);
      final c = ringCentre(tester, 'Patching');
      await tester.tapAt(c); // the number
      await tester.pumpAndSettle();
      expect(opened, isEmpty);
      // 8 of 10 up to date, 2 waiting: three o'clock is inside "Up to date".
      await tester.tapAt(c + const Offset(100.8, 0));
      await tester.pumpAndSettle();
      expect(opened, ['/hosts?health=Healthy']);
    });

    testWidgets('a cost slice asks the page to open that cause, and Healthy is not a link', (tester) async {
      await pump(tester);
      await tester.tap(find.byKey(const Key('legend-Disconnected')));
      expect(anchors, [(AutonomyAnchor.causes, 'disconnected')]);
      expect(find.byKey(const Key('legend-Healthy')), findsNothing);
    });

    testWidgets('a CVE slice opens the CVE list narrowed to that CVE', (tester) async {
      await pump(tester, cves: [_cve('CVE-2026-0001', ['A', 'B'])]);
      await tester.tap(find.byKey(const Key('legend-CVE-2026-0001')));
      await tester.pumpAndSettle();
      expect(opened, ['/vulnerabilities?exploited=0&cve=CVE-2026-0001']);
    });
  });
}
