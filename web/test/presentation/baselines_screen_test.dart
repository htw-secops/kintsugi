import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/baseline_mapper.dart';
import 'package:kintsugi_web/domain/usecases/baseline_usecases.dart';
import 'package:kintsugi_web/presentation/baselines/baselines_screen.dart';

import 'fake_baseline_repository.dart';

/// The list: one row per baseline, its signed version, and its compliance counts.
void main() {
  late FakeBaselineRepository repository;

  setUp(() {
    repository = FakeBaselineRepository(baselines: [
      baselineSummaryFromJson({
        'id': 'b1',
        'name': 'Windows workstation (Master Daily)',
        'latestVersion': 2,
        'signedVersion': 1,
        'assignedToAllWindowsHosts': false,
        'applicableHostCount': 3,
        'settingCount': 33,
        'totals': {'compliant': 80, 'drifted': 7, 'notApplicable': 3, 'error': 1, 'notReported': 8},
        'assignedApplicationName': 'Example Mail Sync',
      }),
    ]);
    locator
      ..registerSingleton(GetBaselines(repository))
      ..registerSingleton(ImportBaseline(repository));
  });

  tearDown(() => locator.reset());

  testWidgets('lists each baseline with its signed version and counts', (tester) async {
    tester.view.physicalSize = const Size(1600, 900);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: BaselinesScreen())));
    await tester.pump();

    expect(find.text('Windows workstation (Master Daily)'), findsOneWidget);
    expect(find.text('v1 (v2 unsigned)'), findsOneWidget);
    expect(find.text('33'), findsOneWidget);
    expect(find.text('7'), findsOneWidget);
    expect(find.text('With Example Mail Sync · 3 host(s)'), findsOneWidget);

    // Dispose so the polling bloc closes before the fake clock is checked for pending timers.
    await tester.pumpWidget(const SizedBox());
  });

  test('importing refuses text that is not a JSON object before anything is sent', () async {
    await expectLater(ImportBaseline(repository).call('[1, 2]'), throwsFormatException);
    expect(repository.created, isNull);

    await ImportBaseline(repository).call('{"name": "b", "items": []}');
    expect(repository.created?['name'], 'b');
  });

  test('an outlook-signature item round-trips through import untouched', () async {
    // Items are authored as pasted JSON, and ImportBaseline sends the decoded object as it is, so the
    // new kind's fields must reach the server byte for byte — the server's validator is what judges them.
    const item = outlookSignatureItemExample;
    await ImportBaseline(repository).call('{"name": "Signatures", "items": [$item]}');

    final sent = (repository.created!['items'] as List).single as Map<String, dynamic>;
    expect(sent['kind'], 'outlook-signature');
    expect(sent['signatureName'], 'Example Signature');
    expect(sent['mode'], 'enforce');
    expect(sent['scope'], 'user');
    expect(sent['path'], r'Software\Microsoft\Office\16.0\Outlook');

    await ImportBaseline(repository).call('{"items": [$item]}', baselineId: 'b1');
    expect(((repository.created!['items'] as List).single as Map)['signatureName'], 'Example Signature');
  });
}
