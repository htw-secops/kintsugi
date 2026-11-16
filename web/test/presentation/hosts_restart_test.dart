import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_screen.dart';

class _Hosts implements HostRepository {
  _Hosts(this.hosts);

  final List<HostSummary> hosts;

  @override
  Future<List<HostSummary>> list() async => hosts;

  @override
  Future<void> requestRemoval(String id) async {}
  @override
  Future<AssignedPerson> assignedPerson(String id) async => const AssignedPerson();

  @override
  Future<int> updateProfiles({
    required List<String> hostIds,
    String? organization,
    String? department,
    String? location,
  }) async =>
      hostIds.length;

  @override
  Future<HostProfileImportResult> importProfiles(List<HostProfileImportRow> rows) async =>
      const HostProfileImportResult(matched: 0, notFound: 0, updated: 0);
}

/// The OS update state when a Linux host has said it needs a restart: the updates are installed but
/// not all in effect, which "Up To Date" would hide. The table has no OS Update column any more, so it
/// is read in the host's details panel.
void main() {
  tearDown(() => locator.reset());

  Future<void> pump(WidgetTester tester, List<Map<String, dynamic>> hosts) async {
    locator
      ..registerSingleton(GetHosts(_Hosts([for (final h in hosts) hostFromJson(h)])))
      ..registerSingleton(RequestHostRemoval(_Hosts(const [])))
      ..registerSingleton(UpdateHostProfiles(_Hosts(const [])));
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: HostsScreen())));
    await tester.pump();
    addTearDown(() => tester.pumpWidget(const SizedBox()));
  }

  /// Opens a host's details panel, after checking the table itself no longer carries the state.
  Future<void> openDetails(WidgetTester tester, String name) async {
    expect(find.text('RESTART NEEDED'), findsNothing, reason: 'there is no OS Update column in the table');
    await tester.tap(find.text(name));
    await tester.pumpAndSettle();
  }

  Map<String, dynamic> host(String name, {bool? update, String? restartSince}) => {
        'id': name,
        'hostname': name,
        'serialNumber': 'S-$name',
        'status': 1,
        'operatingSystemUpdateAvailable': update,
        if (restartSince != null) ...{
          'restartRequiredSinceUtc': restartSince,
          'restartRequiredBecause': 'linux-image-6.8.0-52-generic',
        },
      };

  testWidgets('an up to date host that needs a restart shows Restart Needed instead of Up To Date', (tester) async {
    await pump(tester, [host('kernel', update: false, restartSince: '2026-11-05T10:00:00Z')]);
    await openDetails(tester, 'kernel');

    expect(find.text('RESTART NEEDED'), findsOneWidget);
    expect(find.text('UP TO DATE'), findsNothing);
  });

  testWidgets('a host with an update waiting and a restart needed shows both', (tester) async {
    await pump(tester, [host('both', update: true, restartSince: '2026-11-05T10:00:00Z')]);
    await openDetails(tester, 'both');

    expect(find.text('UPDATE AVAILABLE'), findsOneWidget);
    expect(find.text('RESTART NEEDED'), findsOneWidget);
  });

  testWidgets('a host that needs no restart looks as it did before', (tester) async {
    await pump(tester, [host('plain', update: false)]);
    await openDetails(tester, 'plain');

    expect(find.text('UP TO DATE'), findsOneWidget);
    expect(find.text('RESTART NEEDED'), findsNothing);
  });

  test('the mapper reads when and why', () {
    final h = hostFromJson(host('x', restartSince: '2026-11-05T10:00:00Z'));

    expect(h.restartRequired, isTrue);
    expect(h.restartRequiredBecause, 'linux-image-6.8.0-52-generic');
    expect(hostFromJson(host('y')).restartRequired, isFalse);
  });
}
