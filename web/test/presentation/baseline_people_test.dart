import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/baseline_mapper.dart';
import 'package:kintsugi_web/domain/entities/baseline.dart';
import 'package:kintsugi_web/domain/usecases/baseline_usecases.dart';
import 'package:kintsugi_web/presentation/baselines/baseline_host_screen.dart';
import 'package:kintsugi_web/presentation/baselines/baseline_people_screen.dart';
import 'package:kintsugi_web/presentation/baselines/baseline_screen.dart';

import 'fake_baseline_repository.dart';

/// IP-875: the per-person baseline screens. Every name, address and application is a placeholder —
/// this repository is public.
void main() {
  const signatureItem = {
    'key': 'outlook.signature',
    'kind': 'outlook-signature',
    'scope': 'user',
    'hive': 'HKCU',
    'path': r'Software\Microsoft\Office\16.0\Outlook',
    'valueName': '',
    'mode': 'enforce',
    'signatureName': 'Example Signature',
  };

  BaselinePersonRow person(String user, {String? email, String? resolvedBy, int? state, String? reason, String host = 'h1'}) =>
      baselinePersonRowFromJson({
        'hostId': host,
        'hostname': host == 'h1' ? 'pc-1' : 'pc-2',
        'windowsUser': user,
        'email': email,
        'resolvedBy': resolvedBy,
        'reason': reason,
        'copyRenderedAtUtc': email == null ? null : '2026-11-18T01:00:00Z',
        'itemKey': 'outlook.signature',
        'state': state,
        'lastSeenUtc': '2026-11-18T03:00:00Z',
      });

  BaselineDetail detail({String? application, int applicationHosts = 0}) => baselineDetailFromJson({
        'id': 'b1',
        'name': 'Outlook signatures',
        'assignedToAllWindowsHosts': false,
        'assignedHostNames': [],
        'versions': [
          {'id': 'v1', 'number': 1, 'document': '{}', 'documentSha256': 'x', 'signed': true, 'createdUtc': '2026-11-18T00:00:00Z'},
        ],
        'items': [
          {
            'item': signatureItem,
            'counts': {'compliant': 1, 'drifted': 1, 'notApplicable': 0, 'error': 0, 'notReported': 0},
          },
        ],
        'hosts': [],
        'assignedApplicationName': application,
        'assignedApplicationHostCount': applicationHosts,
      });

  late FakeBaselineRepository repository;

  void register() {
    locator
      ..registerSingleton(GetBaseline(repository))
      ..registerSingleton(GetBaselineHost(repository))
      ..registerSingleton(GetBaselinePeople(repository))
      ..registerSingleton(SearchInstalledApplicationNames(repository))
      ..registerSingleton(ImportBaseline(repository))
      ..registerSingleton(SignBaselineVersion(repository))
      ..registerSingleton(AssignBaseline(repository));
  }

  tearDown(() => locator.reset());

  Future<void> pump(WidgetTester tester, Widget screen) async {
    tester.view.physicalSize = const Size(1700, 2400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(theme: AppTheme.light(), home: Scaffold(body: screen)),
    );
    await tester.pump();
  }

  // Dispose so the polling bloc closes before the fake clock is checked for pending timers.
  Future<void> dispose(WidgetTester tester) => tester.pumpWidget(const SizedBox());

  testWidgets('the People view lists each session with how it resolved and its state', (tester) async {
    repository = FakeBaselineRepository(peopleRows: [
      person('ana.ex', email: 'ana@example.com', resolvedBy: 'gcpw', state: 0),
      person('shared', reason: 'more than one email matches'),
    ]);
    register();

    await pump(tester, const BaselinePeopleScreen(baselineId: 'b1'));

    expect(find.text('ana.ex'), findsOneWidget);
    expect(find.text('ana@example.com'), findsOneWidget);
    expect(find.text('GCPW'), findsOneWidget);
    expect(find.text('COMPLIANT'), findsOneWidget);
    expect(find.text('Unresolved'), findsOneWidget);
    expect(find.text('more than one email matches'), findsOneWidget);
    expect(find.text('Not reported'), findsOneWidget);
    expect(find.text('No copy'), findsOneWidget);
    expect(find.text('pc-1'), findsNWidgets(2));
    expect(tester.takeException(), isNull);

    await dispose(tester);
  });

  testWidgets('the host page shows only that host\'s people, under the per-person item', (tester) async {
    repository = FakeBaselineRepository(
      hostDetail: baselineHostDetailFromJson({
        'hostId': 'h1',
        'hostname': 'pc-1',
        'signedVersion': 1,
        'items': [
          {'item': signatureItem, 'state': 1},
        ],
        'facts': [],
      }),
      peopleRows: [
        person('ana.ex', email: 'ana@example.com', resolvedBy: 'username', state: 1),
        person('ben.ex', email: 'ben@example.com', resolvedBy: 'gcpw', state: 0, host: 'h2'),
      ],
    );
    register();

    await pump(tester, const BaselineHostScreen(baselineId: 'b1', hostId: 'h1'));

    expect(find.text('Outlook signature "Example Signature"'), findsOneWidget);
    expect(find.text('PEOPLE'), findsOneWidget);
    expect(find.text('ana.ex'), findsOneWidget);
    expect(find.text('ben.ex'), findsNothing);
    expect(repository.peopleReads, 1);
    expect(tester.takeException(), isNull);

    await dispose(tester);
  });

  testWidgets('a host page with no per-person item does not read the people route', (tester) async {
    repository = FakeBaselineRepository(
      hostDetail: baselineHostDetailFromJson({
        'hostId': 'h1',
        'hostname': 'pc-1',
        'signedVersion': 1,
        'items': [
          {
            'item': {'key': 'a', 'kind': 'setting', 'scope': 'machine', 'hive': 'HKLM', 'path': 'P', 'valueName': 'V'},
            'state': 0,
          },
        ],
        'facts': [],
      }),
    );
    register();

    await pump(tester, const BaselineHostScreen(baselineId: 'b1', hostId: 'h1'));

    expect(find.text('PEOPLE'), findsNothing);
    expect(repository.peopleReads, 0);

    await dispose(tester);
  });

  testWidgets('a baseline with a signature item renders it with its counts and offers People', (tester) async {
    repository = FakeBaselineRepository(detail: detail(application: 'Example Mail Sync', applicationHosts: 3));
    register();

    await pump(tester, const BaselineScreen(baselineId: 'b1'));

    expect(find.text('outlook.signature'), findsOneWidget);
    expect(find.text('Outlook signature "Example Signature"'), findsOneWidget);
    expect(find.text('enforce · per user'), findsOneWidget);
    expect(find.text('VIEW PEOPLE'), findsOneWidget);
    expect(find.text('Example Mail Sync: 3 Windows host(s) match'), findsOneWidget);
    expect(tester.takeException(), isNull);

    await dispose(tester);
  });

  testWidgets('the application picker searches, and Save sends the picked name', (tester) async {
    repository = FakeBaselineRepository(
      detail: detail(),
      applicationNames: ['Example Mail Sync', 'Example Notes'],
    );
    register();

    await pump(tester, const BaselineScreen(baselineId: 'b1'));
    expect(find.text('No application chosen.'), findsOneWidget);

    await tester.enterText(find.byKey(const ValueKey('application-picker')), 'mail');
    await tester.pumpAndSettle();
    expect(repository.namesQueried, contains('mail'));

    await tester.tap(find.text('Example Mail Sync').last);
    await tester.pumpAndSettle();
    expect(find.text('Example Mail Sync: not saved yet, so the match count shows after Save Assignment'), findsOneWidget);

    await tester.tap(find.text('SAVE ASSIGNMENT'));
    await tester.pump();
    expect(repository.assigned?['applicationName'], 'Example Mail Sync');

    await dispose(tester);
  });

  test('a blank application name is sent as none, and a blank search asks for nothing', () async {
    repository = FakeBaselineRepository(applicationNames: ['Example Mail Sync']);

    await AssignBaseline(repository).call('b1', allWindowsHosts: false, hostNames: const [], applicationName: '  ');
    expect(repository.assigned?['applicationName'], isNull);

    expect(await SearchInstalledApplicationNames(repository).call('   '), isEmpty);
    expect(repository.namesQueried, isEmpty);
  });
}
