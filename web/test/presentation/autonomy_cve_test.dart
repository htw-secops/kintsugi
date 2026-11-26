import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/autonomy_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/vulnerability.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_cve.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_rings.dart';

VulnerabilityFinding _cve(String id, {double? score, bool exploited = false, List<String> apps = const ['A'], DateTime? published}) =>
    VulnerabilityFinding(
      cveId: id,
      description: null,
      cvssBaseScore: score,
      cvssSeverity: null,
      cvssVector: null,
      cvssVersion: null,
      cvssDerivedFromVector: false,
      publishedUtc: published,
      knownExploited: exploited,
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

void main() {
  final now = DateTime.utc(2026, 11, 26, 12);

  group('cveRing', () {
    test('counts only CVEs that need attention and ranks them by applications reached', () {
      final ring = cveRing([
        _cve('CVE-1', score: 9.8, apps: ['A', 'B', 'C']),
        _cve('CVE-2', score: 5, apps: ['A', 'B', 'C', 'D']), // medium: left out
        _cve('CVE-3', exploited: true, apps: ['A']),
        _cve('CVE-4', score: null, apps: ['A', 'B']), // unscored, not exploited: left out
        _cve('CVE-5', score: 7, apps: ['A', 'A', 'B']), // one app twice counts once
      ]);
      expect(ring.centre, '3');
      expect(ring.slices.map((s) => s.label), ['CVE-1', 'CVE-5', 'CVE-3 (exploited)']);
      expect(ring.slices.map((s) => s.value), [3, 2, 1]);
    });

    test('folds everything after the named ones into Other CVEs, and says none when empty', () {
      final many = [for (var i = 0; i < 6; i++) _cve('CVE-$i', score: 8, apps: [for (var a = 0; a <= 6 - i; a++) 'app$a'])];
      final ring = cveRing(many);
      expect(ring.slices.length, cveRingNamed + 1);
      expect(ring.slices.last.label, 'Other CVEs');
      expect(ring.slices.last.value, 3 + 2);
      expect(cveRing([]).note, 'none need attention');
      expect(cveRing([]).centre, '0');
    });
  });

  group('calendar maths', () {
    test('counts per UTC day and ignores CVEs with no date', () {
      final counts = cveDayCounts([
        _cve('a', published: DateTime.utc(2026, 11, 1, 3)),
        _cve('b', published: DateTime.utc(2026, 11, 1, 23)),
        _cve('c'),
      ]);
      expect(counts, {DateTime.utc(2026, 11, 1): 2});
    });

    test('starts on a Sunday exactly 51 weeks before this week', () {
      final s = calendarStart(now); // 26 Nov 2026 is a Thursday
      expect(s.weekday, DateTime.sunday);
      expect(DateTime.utc(2026, 11, 22).difference(s).inDays, 51 * 7);
    });

    test('the legend counts only days the calendar shows', () {
      // Covered by the widget below; here the limit note alone.
      expect(cveLimitNote(500, 2183), ' · 500 most-installed of 2183');
      expect(cveLimitNote(500, 500), '');
      expect(cveLimitNote(500, null), '');
    });

    test('levels run 0 to 4 against the busiest day', () {
      expect(cveLevel(0, 10), 0);
      expect(cveLevel(1, 100), 1);
      expect(cveLevel(10, 10), 4);
      expect(cveLevel(5, 10), 2);
    });
  });

  group('widgets', () {
    Future<void> pump(WidgetTester tester, List<VulnerabilityFinding>? findings, {Size size = const Size(1500, 1400), int? total}) async {
      tester.view.physicalSize = size;
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final data = autonomyFromJson(_minimal);
      await tester.pumpWidget(MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(body: SingleChildScrollView(child: AutonomyRings(data: data, findings: findings, total: total, now: now))),
      ));
      await tester.pumpAndSettle();
    }

    testWidgets('without CVEs there are three rings and no calendar', (tester) async {
      await pump(tester, null);
      expect(find.byKey(const Key('ring-Patching')), findsOneWidget);
      expect(find.byKey(const Key('ring-CVEs hitting most apps')), findsNothing);
      expect(find.byKey(const Key('cve-calendar')), findsNothing);
    });

    testWidgets('with CVEs there are four rings of one size and a calendar as wide as them', (tester) async {
      await pump(tester, [_cve('CVE-1', score: 9, apps: ['A', 'B'], published: DateTime.utc(2026, 11, 20))]);
      final keys = ['Patching', 'Device health', 'Cost of slow patching', 'CVEs hitting most apps'];
      final sizes = [for (final k in keys) tester.getSize(find.byKey(Key('ring-$k')))];
      for (final s in sizes) {
        expect(s, sizes.first);
      }
      final calendar = tester.getRect(find.byKey(const Key('cve-calendar')));
      final first = tester.getRect(find.byKey(Key('ring-${keys.first}')));
      final last = tester.getRect(find.byKey(Key('ring-${keys.last}')));
      expect(calendar.left, first.left);
      expect(calendar.right, last.right);
      expect(calendar.top, greaterThan(first.bottom));
      expect(calendar.height, lessThan(first.height / 2)); // thin
      expect(find.text('CVE-1'), findsOneWidget);
    });

    testWidgets('the calendar fits its card: it does not scroll sideways, at any wide size', (tester) async {
      for (final width in [1850.0, 1500.0]) {
        await pump(tester, [_cve('CVE-1', score: 9, published: DateTime.utc(2026, 11, 20))], size: Size(width, 1400));
        final scroll = tester.state<ScrollableState>(find.descendant(of: find.byKey(const Key('cve-calendar')), matching: find.byType(Scrollable)));
        expect(scroll.position.maxScrollExtent, closeTo(0, 0.01), reason: 'at $width px');
      }
    });

    testWidgets('the busiest day is worked out over the days drawn, not over old CVEs', (tester) async {
      await pump(tester, [
        for (var i = 0; i < 40; i++) _cve('OLD-$i', published: DateTime.utc(2023, 5, 5)),
        _cve('NEW-1', published: DateTime.utc(2026, 11, 20)),
      ]);
      expect(find.text('More (busiest day: 1)'), findsOneWidget);
    });

    testWidgets('says how many CVEs it counted when the fleet has more', (tester) async {
      await pump(tester, [_cve('CVE-1', score: 9, published: DateTime.utc(2026, 11, 20))], total: 2183);
      expect(find.textContaining('1 most-installed of 2183'), findsNWidgets(2)); // the ring and the calendar
    });
  });
}

const _minimal = <String, dynamic>{
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
  'causes': [],
  'kpis': [],
  'events': [],
};
