import 'dart:async';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/platform/file_picker.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/patching_policy_screen.dart';

/// The PNG signature and some padding. The screen checks the signature, not that the rest decodes;
/// the preview's errorBuilder covers that it does not.
Uint8List _png([int length = 32]) {
  final bytes = Uint8List(length);
  bytes.setAll(0, const [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
  return bytes;
}

class _FakeFilePicker implements FilePicker {
  Uint8List? next;
  String? lastAccept;
  int? lastMaxBytes;

  /// Refuses an oversized file the way the browser's picker does, before handing any of it over.
  @override
  Future<Uint8List?> pick({required String accept, required int maxBytes}) async {
    lastAccept = accept;
    lastMaxBytes = maxBytes;
    final next = this.next;
    if (next != null && next.length > maxBytes) throw FileTooLargeException(next.length);
    return next;
  }
}

class _FakePatchingPolicySettingsRepository implements PatchingPolicySettingsRepository {
  _FakePatchingPolicySettingsRepository(this.stored);

  PatchingPolicySettings stored;
  PatchingPolicySettings? lastUpdate;

  /// When set, [update] answers only once this completes: a save still in flight.
  Completer<void>? inFlight;

  @override
  Future<PatchingPolicySettings> read() async => stored;

  @override
  Future<PatchingPolicySettings> update(PatchingPolicySettings settings) async {
    await inFlight?.future;
    return stored = lastUpdate = settings;
  }
}

/// The Patching Policy screen's "What users see" section, pumped against a fake repository and a
/// fake [FilePicker] — the reason the picker is an interface at all, since `package:web` cannot be
/// loaded under `flutter test`.
///
/// What this pins is the part nothing on the server can see: a file refused at pick time leaves
/// the saved logo alone, and what Save sends is the logo on screen — including none.
void main() {
  late _FakePatchingPolicySettingsRepository repository;
  late _FakeFilePicker picker;

  void register({String? name, Uint8List? logo}) {
    repository = _FakePatchingPolicySettingsRepository(PatchingPolicySettings(
      intervalValue: 7,
      intervalUnit: PatchingTimeUnit.days,
      delayValue: 1,
      delayUnit: PatchingTimeUnit.days,
      maxDelayCount: 3,
      organisationName: name,
      organisationLogoPng: logo,
    ));
    picker = _FakeFilePicker();
    locator
      ..registerSingleton(GetPatchingPolicySettings(repository))
      ..registerSingleton(UpdatePatchingPolicySettings(repository))
      ..registerSingleton<FilePicker>(picker);
  }

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: const Scaffold(body: PatchingPolicySettingsScreen()),
      ),
    );
    await tester.pumpAndSettle();
  }

  // Both button widgets upper-case their labels.
  Future<void> tapButton(WidgetTester tester, String label) async {
    await tester.tap(find.text(label.toUpperCase()));
    await tester.pumpAndSettle();
  }

  testWidgets('a picked PNG is previewed and saved with the name', (tester) async {
    register();
    await pumpScreen(tester);
    expect(find.text('WHAT USERS SEE'), findsOneWidget);
    expect(find.text('REMOVE'), findsNothing);

    final logo = _png();
    picker.next = logo;
    await tapButton(tester, 'Choose PNG');

    expect(picker.lastAccept, '.png,image/png');
    expect(picker.lastMaxBytes, PatchingPolicySettings.maxOrganisationLogoBytes);
    expect(find.byType(Image), findsOneWidget);
    expect(find.text('REPLACE'), findsOneWidget);
    expect(find.text('REMOVE'), findsOneWidget);
    expect(tester.takeException(), isNull);

    await tester.enterText(
      find.widgetWithText(TextField, 'Shown on every popup, e.g. Example Corp IT'),
      'Example Corp IT',
    );
    await tapButton(tester, 'Save Policy');

    expect(repository.lastUpdate?.organisationName, 'Example Corp IT');
    expect(repository.lastUpdate?.organisationLogoPng, logo);
  });

  testWidgets('a file over 64 KB is refused, and the saved logo stays', (tester) async {
    final saved = _png();
    register(logo: saved);
    await pumpScreen(tester);

    picker.next = _png(PatchingPolicySettings.maxOrganisationLogoBytes + 1);
    await tapButton(tester, 'Replace');

    expect(find.text('The logo must be 64 KB or smaller.'), findsOneWidget);
    await tapButton(tester, 'Save Policy');
    expect(repository.lastUpdate?.organisationLogoPng, saved);
  });

  // The Windows agent will not decode past 1024 pixels, so a larger logo would preview here and
  // then appear on no PC.
  testWidgets('a PNG wider than the agent draws is refused, and the saved logo stays', (tester) async {
    final saved = _png();
    register(logo: saved);
    await pumpScreen(tester);

    final wide = _png();
    wide.setAll(8, const [0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]); // IHDR
    ByteData.sublistView(wide, 16, 24)
      ..setUint32(0, 2000)
      ..setUint32(4, 500);
    picker.next = wide;
    await tapButton(tester, 'Replace');

    expect(find.text('The logo must be at most 1024 × 1024 pixels.'), findsOneWidget);
    await tapButton(tester, 'Save Policy');
    expect(repository.lastUpdate?.organisationLogoPng, saved);
  });

  // The save's answer re-fills the form, so anything changed while it was in flight would be
  // silently replaced by what was sent.
  testWidgets('the form is locked while a save is in flight', (tester) async {
    register(logo: _png());
    await pumpScreen(tester);
    repository.inFlight = Completer<void>();
    TextField name() =>
        tester.widget<TextField>(find.widgetWithText(TextField, 'Shown on every popup, e.g. Example Corp IT'));

    await tester.tap(find.text('SAVE POLICY'));
    await tester.pump();

    expect(name().enabled, isFalse);
    await tester.tap(find.text('REPLACE'));
    expect(picker.lastAccept, isNull, reason: 'the picker must not open mid-save');

    repository.inFlight!.complete();
    await tester.pumpAndSettle();
    expect(name().enabled, isTrue);
  });

  testWidgets('a file that is not a PNG is refused', (tester) async {
    register();
    await pumpScreen(tester);

    picker.next = Uint8List.fromList('GIF89a'.codeUnits);
    await tapButton(tester, 'Choose PNG');

    expect(find.text('The logo must be a PNG image.'), findsOneWidget);
    expect(find.byType(Image), findsNothing);
  });

  testWidgets('a cancelled dialog changes nothing', (tester) async {
    register(logo: _png());
    await pumpScreen(tester);

    picker.next = null;
    await tapButton(tester, 'Replace');

    expect(find.text('REMOVE'), findsOneWidget);
  });

  testWidgets('Remove saves no logo, keeping the name', (tester) async {
    register(name: 'Example Corp IT', logo: _png());
    await pumpScreen(tester);
    expect(find.text('Example Corp IT'), findsOneWidget);

    await tapButton(tester, 'Remove');
    expect(find.byType(Image), findsNothing);
    await tapButton(tester, 'Save Policy');

    expect(repository.lastUpdate?.organisationLogoPng, isNull);
    expect(repository.lastUpdate?.organisationName, 'Example Corp IT');
  });
}
