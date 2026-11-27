import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/device_investigation_screen.dart';

class _FakeRepo implements DeviceInvestigationSettingsRepository {
  _FakeRepo(this.stored);

  DeviceInvestigationSettings stored;
  final saves = <Map<String, Object?>>[];
  ApiException? saveError;
  ApiException? testError;
  int tests = 0;

  @override
  Future<DeviceInvestigationSettings> read() async => stored;

  @override
  Future<DeviceInvestigationSettings> update({
    required bool enabled,
    required String? slackWebhookUrl,
    required String? consoleBaseUrl,
    required bool clearSlackWebhook,
  }) async {
    saves.add({
      'enabled': enabled,
      'slackWebhookUrl': slackWebhookUrl,
      'consoleBaseUrl': consoleBaseUrl,
      'clearSlackWebhook': clearSlackWebhook,
    });
    if (saveError != null) throw saveError!;
    return stored = DeviceInvestigationSettings(
      enabled: enabled,
      hasSlackWebhook: !clearSlackWebhook && (slackWebhookUrl != null || stored.hasSlackWebhook),
      consoleBaseUrl: consoleBaseUrl,
    );
  }

  @override
  Future<void> sendTestAlert() async {
    tests++;
    if (testError != null) throw testError!;
  }
}

void main() {
  late _FakeRepo repo;

  tearDown(() => locator.reset());

  Future<void> pump(WidgetTester tester, DeviceInvestigationSettings stored) async {
    repo = _FakeRepo(stored);
    locator
      ..registerSingleton(GetDeviceInvestigationSettings(repo))
      ..registerSingleton(UpdateDeviceInvestigationSettings(repo))
      ..registerSingleton(SendDeviceInvestigationTestAlert(repo));
    tester.view.physicalSize = const Size(1400, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await tester.pumpWidget(
      MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: DeviceInvestigationSettingsScreen())),
    );
    await tester.pumpAndSettle();
  }

  final sendTest = find.widgetWithText(SecondaryButton, 'Send test message');
  TextField field(WidgetTester tester, int i) => tester.widgetList<TextField>(find.byType(TextField)).elementAt(i);

  testWidgets('off by default; saving a webhook turns it on, clears the field, and enables the test', (tester) async {
    await pump(tester, const DeviceInvestigationSettings(enabled: false, hasSlackWebhook: false, consoleBaseUrl: null));

    expect(find.textContaining('Off until an administrator turns it on'), findsOneWidget);
    expect(tester.widget<SecondaryButton>(sendTest).onPressed, isNull, reason: 'nothing saved to send to');
    expect(find.text('Remove webhook'), findsNothing);
    expect(field(tester, 0).obscureText, isTrue);

    await tester.tap(find.text('Investigate unstable devices automatically'));
    await tester.enterText(find.byType(TextField).at(0), 'https://hooks.slack.com/services/T/B/x');
    await tester.enterText(find.byType(TextField).at(1), 'https://k.example.com');
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(repo.saves.single, {
      'enabled': true,
      'slackWebhookUrl': 'https://hooks.slack.com/services/T/B/x',
      'consoleBaseUrl': 'https://k.example.com',
      'clearSlackWebhook': false,
    });
    expect(find.text('Device investigation settings saved.'), findsOneWidget);
    expect(field(tester, 0).controller!.text, isEmpty, reason: 'the webhook is never kept in the form');
    expect(field(tester, 0).decoration!.hintText, 'Saved — leave blank to keep the saved one');
    expect(tester.widget<SecondaryButton>(sendTest).onPressed, isNotNull);
  });

  testWidgets("a refused save shows the server's reason", (tester) async {
    await pump(tester, const DeviceInvestigationSettings(enabled: false, hasSlackWebhook: false, consoleBaseUrl: null));
    repo.saveError = const ApiException('The Slack webhook must be an absolute https:// URL.', statusCode: 400);

    await tester.enterText(find.byType(TextField).at(0), 'http://hooks.slack.com/x');
    await tester.tap(find.text('Save'));
    await tester.pumpAndSettle();

    expect(find.text('The Slack webhook must be an absolute https:// URL.'), findsOneWidget);
  });

  testWidgets('with a webhook saved: the test shows the outcome, and Remove saves with clearSlackWebhook', (tester) async {
    await pump(tester, const DeviceInvestigationSettings(enabled: true, hasSlackWebhook: true, consoleBaseUrl: 'https://k.example.com'));

    expect(field(tester, 1).controller!.text, 'https://k.example.com');

    repo.testError = const ApiException('The webhook did not accept the message: 404 no_team', statusCode: 502);
    await tester.tap(sendTest);
    await tester.pumpAndSettle();
    expect(find.text('The webhook did not accept the message: 404 no_team'), findsOneWidget);

    repo.testError = null;
    await tester.tap(sendTest);
    await tester.pumpAndSettle();
    expect(repo.tests, 2);
    expect(find.text('Slack accepted the test message.'), findsOneWidget);
    expect(find.textContaining('did not accept'), findsNothing);

    await tester.tap(find.text('Remove webhook'));
    await tester.pumpAndSettle();
    expect(repo.saves.single, {
      'enabled': true,
      'slackWebhookUrl': null,
      'consoleBaseUrl': 'https://k.example.com',
      'clearSlackWebhook': true,
    });
    expect(find.text('Remove webhook'), findsNothing);
    expect(tester.widget<SecondaryButton>(sendTest).onPressed, isNull);
  });
}
