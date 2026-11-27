import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/autonomy_mapper.dart';
import 'package:kintsugi_web/domain/entities/autonomy.dart';
import 'package:kintsugi_web/presentation/autonomy/autonomy_kev.dart';

KevBacklogPoint _day(DateTime d, {int open = 40, int within = 25, int overdue = 15, int due = 6, int notSeen = 9, String? cve = 'CVE-2026-0001', int devices = 31}) =>
    KevBacklogPoint(
      dateUtc: d,
      devicesOpen: open,
      withinTarget: within,
      overdue: overdue,
      dueWithin7Days: due,
      overdueNotSeen7Days: notSeen,
      topCveId: cve,
      topCveDevices: devices,
    );

final _today = DateTime.utc(2026, 11, 27);

KevBacklog _backlog({int recorded = 0, KevBacklogPoint? today}) => KevBacklog(
      series: [for (var i = recorded; i > 0; i--) _day(_today.subtract(Duration(days: i)), open: 30 + i, within: 20, overdue: 10 + i)],
      today: today ?? _day(_today),
      target: 'x',
    );

void main() {
  test('the mapper reads the server\'s answer, and is happy with a day nobody recorded yet', () {
    final kev = kevBacklogFromJson({
      'series': [
        {'dateUtc': '2026-11-26T00:00:00+00:00', 'devicesOpen': 5, 'withinTarget': 3, 'overdue': 2, 'dueWithin7Days': 1, 'overdueNotSeen7Days': 1, 'topCveId': 'CVE-2026-0001', 'topCveDevices': 4},
      ],
      'today': {'dateUtc': '2026-11-27T00:00:00+00:00', 'devicesOpen': 6, 'withinTarget': 3, 'overdue': 3, 'dueWithin7Days': 0, 'overdueNotSeen7Days': 0, 'topCveId': null, 'topCveDevices': 0},
      'target': 'A device is overdue when…',
    });

    expect(kev.points.length, 2);
    expect(kev.series.single.dateUtc, DateTime.utc(2026, 11, 26));
    expect(kev.today.overdue, 3);
    expect(kev.today.topCveId, isNull);
    expect(kevBacklogFromJson({'today': <String, dynamic>{}}).series, isEmpty);
  });

  group('Patch Tuesday', () {
    test('is the second Tuesday of each month in the range', () {
      expect(patchTuesdays(DateTime.utc(2026, 10, 3), DateTime.utc(2026, 11, 27)), [DateTime.utc(2026, 10, 13), DateTime.utc(2026, 11, 10)]);
    });

    test('leaves out a month whose Patch Tuesday falls outside the range, and includes both ends', () {
      expect(patchTuesdays(DateTime.utc(2026, 11, 11), DateTime.utc(2026, 12, 7)), isEmpty);
      expect(patchTuesdays(DateTime.utc(2026, 11, 10), DateTime.utc(2026, 11, 10)), [DateTime.utc(2026, 11, 10)]);
    });
  });

  group('the axis', () {
    test('is three round steps above the highest value', () {
      expect(kevAxis(152), (max: 300.0, step: 100.0));
      expect(kevAxis(7), (max: 15.0, step: 5.0));
      expect(kevAxis(433).max, greaterThanOrEqualTo(433));
    });

    test('still has a scale with nothing to show', () {
      expect(kevAxis(0), (max: 3.0, step: 1.0));
    });
  });

  group('hovering', () {
    test('finds the nearest day, and stays inside the days drawn', () {
      expect(kevIndexAt(0, 300, 4), 0);
      expect(kevIndexAt(300, 300, 4), 3);
      expect(kevIndexAt(160, 300, 4), 2);
      expect(kevIndexAt(-20, 300, 4), 0);
      expect(kevIndexAt(999, 300, 4), 3);
      expect(kevIndexAt(50, 300, 1), 0);
    });
  });

  test('coverage is low when fewer than half the subjects are assessed', () {
    expect(kevCoverageLow(3, 1203), isTrue);
    expect(kevCoverageLow(900, 100), isFalse);
    expect(kevCoverageLow(5, 5), isFalse);
  });

  group('the card', () {
    Future<void> pump(WidgetTester tester, KevBacklog backlog, {int? assessed, int? notAssessed, GoRouter? router}) async {
      tester.view.physicalSize = const Size(900, 900);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final home = Scaffold(
        body: SingleChildScrollView(child: KevBacklogChart(backlog: backlog, assessed: assessed, notAssessed: notAssessed)),
      );
      await tester.pumpWidget(
        router == null
            ? MaterialApp(theme: AppTheme.light(), home: home)
            : MaterialApp.router(theme: AppTheme.light(), routerConfig: router),
      );
      await tester.pump();
    }

    testWidgets('says it started today when there is only one day, and draws no history', (tester) async {
      await pump(tester, _backlog());

      expect(tester.takeException(), isNull);
      expect(find.byKey(const Key('kev-first-day')), findsOneWidget);
      expect(find.text('Recording started today. The chart fills in one day at a time.'), findsOneWidget);
    });

    testWidgets('has no first-day note once there is a history', (tester) async {
      await pump(tester, _backlog(recorded: 5));

      expect(tester.takeException(), isNull);
      expect(find.byKey(const Key('kev-first-day')), findsNothing);
    });

    testWidgets('shows today\'s figures beside the chart', (tester) async {
      await pump(tester, _backlog());

      expect(find.descendant(of: find.byKey(const Key('kev-within')), matching: find.text('25')), findsOneWidget);
      expect(find.descendant(of: find.byKey(const Key('kev-overdue')), matching: find.text('15')), findsOneWidget);
      expect(find.text('+6 due'), findsOneWidget);
      expect(find.text('9 not seen in 7+ days'), findsOneWidget);
    });

    testWidgets('leaves out the due and not-seen lines when there are none', (tester) async {
      await pump(tester, _backlog(today: _day(_today, due: 0, notSeen: 0)));

      expect(find.byKey(const Key('kev-due')), findsNothing);
      expect(find.byKey(const Key('kev-not-seen')), findsNothing);
    });

    testWidgets('warns when most of the fleet is unassessed, and only then', (tester) async {
      await pump(tester, _backlog(), assessed: 3, notAssessed: 1203);
      expect(find.byKey(const Key('kev-coverage')), findsOneWidget);
      expect(find.textContaining('Only 3 of 1206 applications and operating systems have been assessed'), findsOneWidget);
    });

    testWidgets('has no warning for a well-assessed fleet, or when coverage is unknown', (tester) async {
      await pump(tester, _backlog(), assessed: 900, notAssessed: 100);
      expect(find.byKey(const Key('kev-coverage')), findsNothing);
    });

    testWidgets('has no CVE line when no exploited CVE is open', (tester) async {
      await pump(tester, _backlog(today: _day(_today, open: 0, within: 0, overdue: 0, due: 0, notSeen: 0, cve: null, devices: 0)));

      expect(tester.takeException(), isNull);
      expect(find.byKey(const Key('kev-top-cve')), findsNothing);
    });

    testWidgets('the CVE on the most devices opens that CVE on CVE Reporting', (tester) async {
      final router = GoRouter(routes: [
        GoRoute(
          path: '/',
          builder: (_, _) => Scaffold(body: SingleChildScrollView(child: KevBacklogChart(backlog: _backlog()))),
        ),
        GoRoute(path: '/vulnerabilities', builder: (_, state) => Scaffold(body: Text('vulns ${state.uri.query}'))),
      ]);
      await pump(tester, _backlog(), router: router);

      expect(find.textContaining('CVE-2026-0001 · 31 devices'), findsOneWidget);
      await tester.tap(find.byKey(const Key('kev-top-cve')));
      await tester.pumpAndSettle();

      expect(find.text('vulns exploited=0&cve=CVE-2026-0001'), findsOneWidget);
    });

    testWidgets('hovering a day does not throw', (tester) async {
      await pump(tester, _backlog(recorded: 5));
      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      addTearDown(mouse.removePointer);

      await mouse.moveTo(tester.getCenter(find.byType(CustomPaint).last));
      await tester.pump();
      await mouse.moveTo(const Offset(2000, 2000));
      await tester.pump();

      expect(tester.takeException(), isNull);
    });
  });
}
