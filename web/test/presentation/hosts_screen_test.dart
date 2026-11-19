import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/status_chip.dart';
import 'package:kintsugi_web/data/models/host_mapper.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/hosts_screen.dart';

class _FakeHostRepository implements HostRepository {
  _FakeHostRepository(this.hosts);

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

  @override
  Future<void> clearAssignedPerson(String id) async {}
}

/// The Status column: the status chip, with the agent's version in brackets centred on the line
/// beneath — and the Hostname column's search box, which is the only way to find one host in a
/// fleet too big to scroll.
///
/// Pumps the real [HostsScreen] against a fake registered in [locator] the way `injection.dart`
/// registers the real one. The version is the one thing here without a server-side mirror test:
/// `HostDto.AgentVersion` reaches the screen through `hostFromJson`, and where it lands is decided
/// only by this widget.
void main() {
  setUp(() {
    locator
      ..registerSingleton(GetHosts(_FakeHostRepository([
        hostFromJson({
          'id': 'a',
          'hostname': 'alpha',
          'serialNumber': 'A1',
          'status': 1,
          'agentVersion': '0.6.1',
          'lastLoggedInUser': 'alice.smith',
        }),
        hostFromJson({
          'id': 'b',
          'hostname': 'bravo',
          'serialNumber': 'B2',
          'ipAddress': '10.0.0.7',
          'operatingSystem': 'Ubuntu 24.04',
          'status': 2,
        }),
      ])))
      ..registerSingleton(RequestHostRemoval(_FakeHostRepository(const [])))
      ..registerSingleton(UpdateHostProfiles(_FakeHostRepository(const [])));
  });

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: HostsScreen()),
      ),
    );
    await tester.pump();
  }

  /// Disposes the screen so its polling bloc closes before the test's fake clock is checked for
  /// timers still pending.
  Future<void> tearDownScreen(WidgetTester tester) => tester.pumpWidget(const SizedBox());

  testWidgets('a host that reported its agent version shows it centred under the status chip', (tester) async {
    await pumpScreen(tester);

    final chip = tester.getRect(find.ancestor(of: find.text('ONLINE'), matching: find.byType(StatusChip)));
    final version = tester.getRect(find.text('(0.6.1)'));
    expect(version.top, greaterThanOrEqualTo(chip.bottom));
    // Centred on the chip, not on the column: the cell is 140px wide and left-aligned, so a version
    // centred on the column would sit visibly to the right of a short chip.
    expect(version.center.dx, closeTo(chip.center.dx, 1.0));

    // The host whose agent predates the field gets the chip alone — no empty brackets.
    expect(find.text('OFFLINE'), findsOneWidget);
    expect(find.text('()'), findsNothing);
    expect(find.textContaining(RegExp(r'^\(.*\)$')), findsOneWidget);

    await tearDownScreen(tester);
  });

  testWidgets('the search narrows the table by hostname, serial, user, address or OS', (tester) async {
    await pumpScreen(tester);
    expect(find.text('alpha'), findsOneWidget);
    expect(find.text('bravo'), findsOneWidget);
    // The user is shown under the hostname, so a match on it is visible.
    expect(find.text('alice.smith'), findsOneWidget);

    final search = find.byType(TextField);

    // A fragment of the hostname, in the wrong case.
    await tester.enterText(search, 'ALP');
    await tester.pump();
    expect(find.text('alpha'), findsOneWidget);
    expect(find.text('bravo'), findsNothing);
    expect(find.text('1 of 2 host(s) match the search'), findsOneWidget);

    // The tail of a serial number, and an address — neither is the hostname.
    await tester.enterText(search, 'b2');
    await tester.pump();
    expect(find.text('bravo'), findsOneWidget);
    expect(find.text('alpha'), findsNothing);

    await tester.enterText(search, '10.0.0');
    await tester.pump();
    expect(find.text('bravo'), findsOneWidget);
    expect(find.text('alpha'), findsNothing);

    // The last logged-in user and the operating system, which is how somebody asks for a machine.
    await tester.enterText(search, 'ALICE');
    await tester.pump();
    expect(find.text('alpha'), findsOneWidget);
    expect(find.text('bravo'), findsNothing);

    await tester.enterText(search, 'ubuntu');
    await tester.pump();
    expect(find.text('bravo'), findsOneWidget);
    expect(find.text('alpha'), findsNothing);

    // Nothing matching leaves the table — and so the search box — on screen, with a message.
    await tester.enterText(search, 'zulu');
    await tester.pump();
    expect(find.text('No hosts match the search.'), findsOneWidget);
    expect(search, findsOneWidget);

    // Clearing it brings everything back and restores the plain count.
    await tester.enterText(search, '');
    await tester.pump();
    expect(find.text('alpha'), findsOneWidget);
    expect(find.text('bravo'), findsOneWidget);
    expect(find.text('2 host(s) registered'), findsOneWidget);

    await tearDownScreen(tester);
  });
}
