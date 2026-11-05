import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/patch_failure_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/patch_health/patch_health_model.dart';
import 'package:kintsugi_web/presentation/patch_health/patch_health_screen.dart';

HostSummary _host(
  String id,
  String name, {
  HostStatus status = HostStatus.online,
  bool? osUpdate,
  int appUpdates = 0,
}) =>
    HostSummary(
      id: id,
      hostname: name,
      serialNumber: 'SN-$id',
      operatingSystem: 'Windows 11',
      ipAddress: null,
      status: status,
      lastSeenUtc: DateTime.utc(2026, 11, 1),
      agentVersion: null,
      operatingSystemUpdateAvailable: osUpdate,
      operatingSystemLatestVersion: null,
      appUpdatesAvailableCount: appUpdates,
      unpatchedCveCount: 0,
      patchedCveCount: 0,
      removalRequested: false,
    );

PatchFailure _failure(
  String id,
  String hostId,
  String application, {
  bool os = false,
  String resolution = 'Outstanding',
  int count = 2,
}) =>
    patchFailureFromJson({
      'id': id,
      'hostId': hostId,
      'hostname': 'host-$hostId',
      'applicationName': application,
      'details': 'exit 1',
      'firstFailedUtc': '2026-11-01T02:00:00Z',
      'lastFailedUtc': '2026-11-05T02:00:00Z',
      'failureCount': count,
      'resolution': resolution,
      'hasScript': false,
      'canFix': false,
      if (os) ...{
        'isOperatingSystemUpdate': true,
        'displayName': 'Windows Update',
        'causeTitle': 'Not enough disk space',
        'causeExplanation': 'There was not enough room.',
        'suggestedFix': 'Free up space on C:.',
      },
    });

class _Failures implements PatchFailureRepository {
  _Failures(this.rows);

  final List<PatchFailure> rows;

  @override
  Future<List<PatchFailure>> list() async => rows;

  @override
  Future<void> dismiss(String id) async {}
}

class _Hosts implements HostRepository {
  _Hosts(this.rows);

  final List<HostSummary> rows;

  @override
  Future<List<HostSummary>> list() async => rows;

  @override
  noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  group('buildHealthItems', () {
    test('an operating system failure carries the server\'s reason', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Windows', os: true)],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items, hasLength(1));
      expect(items.single.kind, HealthKind.osUpdate);
      expect(items.single.subject, 'Windows Update');
      expect(items.single.cause, 'Not enough disk space');
      expect(items.single.suggestedFix, 'Free up space on C:.');
      expect(items.single.tries, 2);
    });

    test('an application failure gets an honest generic reason', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Slack')],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items.single.kind, HealthKind.application);
      expect(items.single.cause, 'The upgrade script failed');
    });

    test('a settled failure is not listed', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Slack', resolution: 'Dismissed')],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items, isEmpty);
    });

    test('an offline host with updates waiting is listed, and an online one is not', () {
      final items = buildHealthItems(
        failures: const [],
        hosts: [
          _host('h1', 'asleep', status: HostStatus.offline, osUpdate: true),
          _host('h2', 'fine', osUpdate: true),
          _host('h3', 'gone-and-current', status: HostStatus.offline, osUpdate: false),
        ],
      );

      expect(items.map((i) => i.hostname), ['asleep']);
      expect(items.single.kind, HealthKind.offline);
    });

    test('a host already listed for an OS failure is not listed again as offline', () {
      final items = buildHealthItems(
        failures: [_failure('f1', 'h1', 'Windows', os: true)],
        hosts: [_host('h1', 'host-h1', status: HostStatus.offline, osUpdate: true)],
      );

      expect(items, hasLength(1));
      expect(items.single.kind, HealthKind.osUpdate);
    });

    test('the worst come first: operating system, then applications, then offline', () {
      final items = buildHealthItems(
        failures: [_failure('a', 'h2', 'Slack'), _failure('o', 'h1', 'Windows', os: true)],
        hosts: [
          _host('h1', 'host-h1'),
          _host('h2', 'host-h2'),
          _host('h3', 'asleep', status: HostStatus.offline, appUpdates: 1),
        ],
      );

      expect(items.map((i) => i.kind), [HealthKind.osUpdate, HealthKind.application, HealthKind.offline]);
    });

    test('a host failing twice counts once among the hosts needing attention', () {
      final items = buildHealthItems(
        failures: [_failure('a', 'h1', 'Slack'), _failure('o', 'h1', 'Windows', os: true)],
        hosts: [_host('h1', 'host-h1')],
      );

      expect(items, hasLength(2));
      expect(hostsNeedingAttention(items), 1);
    });
  });

  group('the screen', () {
    tearDown(() => locator.reset());

    Future<void> pumpScreen(WidgetTester tester) async {
      locator
        ..registerSingleton(GetPatchFailures(_Failures([
          _failure('o', 'h1', 'Windows', os: true),
          _failure('a', 'h2', 'Slack'),
        ])))
        ..registerSingleton(GetHosts(_Hosts([
          _host('h1', 'host-h1'),
          _host('h2', 'host-h2'),
          _host('h3', 'asleep-host', status: HostStatus.offline, osUpdate: true),
        ])));

      tester.view.physicalSize = const Size(1500, 1200);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);

      await tester.pumpWidget(
        MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: PatchHealthScreen())),
      );
      await tester.pumpAndSettle();
    }

    testWidgets('lists the failing hosts with the reason, and the tabs count them', (tester) async {
      await pumpScreen(tester);

      expect(find.text('host-h1'), findsOneWidget);
      expect(find.text('host-h2'), findsOneWidget);
      expect(find.text('asleep-host'), findsOneWidget);
      expect(find.text('NOT ENOUGH DISK SPACE'), findsOneWidget);
      expect(find.text('ALL (3)'), findsOneWidget);
      expect(find.text('OPERATING SYSTEM (1)'), findsOneWidget);
      expect(find.text('OFFLINE (1)'), findsOneWidget);
    });

    testWidgets('a tab leaves only its kind', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.text('OFFLINE (1)'));
      await tester.pumpAndSettle();

      expect(find.text('asleep-host'), findsOneWidget);
      expect(find.text('host-h1'), findsNothing);
    });

    testWidgets('a row opens to its explanation and what to do', (tester) async {
      await pumpScreen(tester);

      await tester.tap(find.byTooltip('Show the details').first);
      await tester.pumpAndSettle();

      expect(find.text('There was not enough room.'), findsOneWidget);
      expect(find.text('OPEN IN FAILED UPDATES'), findsOneWidget);
    });
  });
}
