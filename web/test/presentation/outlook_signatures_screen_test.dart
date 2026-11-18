import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/outlook_signatures_screen.dart';

class _FakeSource implements OutlookSignatureSourceRepository {
  _FakeSource(this.stored);

  OutlookSignatureSource stored;
  Map<String, Object?>? saved;
  int reads = 0;

  @override
  Future<OutlookSignatureSource> read() async {
    reads++;
    return stored;
  }

  @override
  Future<OutlookSignatureSource> update({
    required bool enabled,
    required String? endpointUrl,
    required String? audience,
    required String? windowsUsernameSuffix,
  }) async {
    saved = {
      'enabled': enabled,
      'endpointUrl': endpointUrl,
      'audience': audience,
      'windowsUsernameSuffix': windowsUsernameSuffix,
    };
    stored = OutlookSignatureSource(
      enabled: enabled,
      endpointUrl: endpointUrl,
      audience: audience,
      windowsUsernameSuffix: windowsUsernameSuffix ?? '',
      isConfigured: true,
      copyCount: stored.copyCount,
    );
    return stored;
  }
}

/// Settings > Outlook Signatures: the four settings, and the last pull shown read-only.
void main() {
  late _FakeSource repository;

  tearDown(() => locator.reset());

  Future<void> pump(WidgetTester tester, OutlookSignatureSource stored) async {
    repository = _FakeSource(stored);
    locator
      ..registerSingleton(GetOutlookSignatureSource(repository))
      ..registerSingleton(UpdateOutlookSignatureSource(repository));
    tester.view.physicalSize = const Size(1400, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: OutlookSignaturesSettingsScreen())),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('shows the stored settings and a failing pull with its error', (tester) async {
    await pump(
      tester,
      OutlookSignatureSource(
        enabled: true,
        endpointUrl: 'https://signatures.example.com/bulk',
        audience: 'example-audience',
        windowsUsernameSuffix: '.ex',
        isConfigured: true,
        lastPullUtc: DateTime.utc(2026, 11, 18, 5),
        lastPullStatus: 'error',
        lastPullCount: 12,
        lastError: 'The source answered 401.',
        copyCount: 12,
      ),
    );

    // The field's own text, read off its controller: the URL box's placeholder is an example URL too.
    final values = tester.widgetList<TextField>(find.byType(TextField)).map((f) => f.controller?.text).toList();
    expect(values, containsAll(['https://signatures.example.com/bulk', 'example-audience', '.ex']));
    expect(find.text('ERROR'), findsOneWidget);
    expect(find.text('The source answered 401.'), findsOneWidget);
    expect(find.text('12'), findsNWidgets(2));
    expect(tester.takeException(), isNull);
  });

  testWidgets('a source that has never pulled says so', (tester) async {
    await pump(
      tester,
      const OutlookSignatureSource(enabled: false, windowsUsernameSuffix: '', isConfigured: false, copyCount: 0),
    );

    expect(find.text('No pull yet'), findsOneWidget);
    expect(find.text('Never'), findsOneWidget);
    expect(find.textContaining('Not configured'), findsOneWidget);
  });

  testWidgets('Save sends the edited values, and Refresh Status re-reads', (tester) async {
    await pump(
      tester,
      const OutlookSignatureSource(enabled: false, windowsUsernameSuffix: '', isConfigured: false, copyCount: 0),
    );

    final fields = find.byType(TextField);
    await tester.enterText(fields.at(0), 'https://signatures.example.com/bulk');
    await tester.enterText(fields.at(1), 'example-audience');
    await tester.tap(find.text('Pull signatures from this source'));
    await tester.tap(find.text('SAVE SETTINGS'));
    await tester.pumpAndSettle();

    expect(repository.saved, {
      'enabled': true,
      'endpointUrl': 'https://signatures.example.com/bulk',
      'audience': 'example-audience',
      'windowsUsernameSuffix': '',
    });
    expect(find.text('Settings saved.'), findsOneWidget);

    final before = repository.reads;
    await tester.tap(find.text('REFRESH STATUS'));
    await tester.pumpAndSettle();
    expect(repository.reads, before + 1);
  });
}
