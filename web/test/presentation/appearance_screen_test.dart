import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/platform/file_picker.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/core/widgets/form_bits.dart';
import 'package:kintsugi_web/domain/entities/appearance.dart';
import 'package:kintsugi_web/presentation/settings/appearance_cubit.dart';
import 'package:kintsugi_web/presentation/settings/appearance_screen.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../core/image_accent_test.dart' show pngOf;
import 'package:kintsugi_web/presentation/shell/logo_spot.dart';
import '../support/appearance_fakes.dart';

class _FakePicker implements FilePicker {
  Uint8List? next;

  @override
  Future<Uint8List?> pick({required String accept, required int maxBytes}) async => next;

  @override
  Future<PickedTextFile?> pickText({required int maxBytes, String accept = ''}) async => null;
}

void main() {
  late FakeAppearanceRepository repository;
  late AppearanceCubit cubit;
  late ThemeCubit theme;

  setUp(() async {
    SharedPreferences.setMockInitialValues({});
    repository = FakeAppearanceRepository();
    cubit = fakeAppearanceCubit(repository: repository, organisationName: 'Example Corp');
    theme = ThemeCubit(await SharedPreferences.getInstance());
    locator.registerSingleton<FilePicker>(_FakePicker());
  });

  tearDown(() => locator.reset());

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 1800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    await cubit.load();
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [BlocProvider.value(value: cubit), BlocProvider.value(value: theme)],
        child: MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: AppearanceSettingsScreen())),
      ),
    );
    await tester.pump();
  }

  testWidgets('choosing a colour previews at once and saves only on Save', (tester) async {
    await pumpScreen(tester);
    expect(tester.widget<KintsugiTextField>(find.byKey(const Key('appearance-org-name'))).controller.text, 'Example Corp');

    await tester.tap(find.byKey(const Key('appearance-preset-gold')));
    await tester.pump();

    expect(cubit.state.preview, const AppearanceSettings(presetKey: 'gold'));
    expect(repository.saves, isEmpty);

    await tester.tap(find.byKey(const Key('appearance-save')));
    await tester.pumpAndSettle();

    expect(repository.saves.single, const AppearanceSettings(presetKey: 'gold'));
    expect(cubit.state.saved.presetKey, 'gold');
    expect(cubit.state.preview, isNull);
    expect(find.text('Settings saved.'), findsOneWidget);
  });

  testWidgets('an invalid colour is explained and cannot be saved; a valid one is normalised', (tester) async {
    await pumpScreen(tester);
    await tester.tap(find.byKey(const Key('appearance-preset-gold')));
    await tester.pump();

    await tester.enterText(find.byKey(const Key('appearance-accent')), '#12G456');
    await tester.pump();
    expect(find.text('Use six hex digits, for example #7C3AED.'), findsOneWidget);
    expect(tester.widget<PrimaryButton>(find.byKey(const Key('appearance-save'))).onPressed, isNull);

    await tester.enterText(find.byKey(const Key('appearance-accent')), '7c3aed');
    await tester.pump();
    expect(cubit.state.preview?.accentHex, '#7C3AED');
    expect(find.text('Use six hex digits, for example #7C3AED.'), findsNothing);
  });

  testWidgets('Reset drops the preview', (tester) async {
    await pumpScreen(tester);
    await tester.tap(find.byKey(const Key('appearance-preset-ocean')));
    await tester.pump();
    expect(cubit.state.preview, isNotNull);

    await tester.tap(find.byKey(const Key('appearance-reset')));
    await tester.pump();
    expect(cubit.state.effective, AppearanceSettings.defaults);
  });

  testWidgets('leaving the screen without saving drops the preview', (tester) async {
    await pumpScreen(tester);
    await tester.tap(find.byKey(const Key('appearance-preset-forest')));
    await tester.pump();

    await tester.pumpWidget(const SizedBox());
    await tester.pump();
    expect(cubit.state.preview, isNull);
  });

  test("this browser's own light/dark pick wins over the company default, except while previewing", () {
    const company = AppearanceState(saved: AppearanceSettings(colourMode: 'light'));
    expect(company.modeFor(browserChose: false, browserMode: ThemeMode.dark), ThemeMode.light);
    expect(company.modeFor(browserChose: true, browserMode: ThemeMode.dark), ThemeMode.dark);

    final previewing = company.copyWith(preview: const AppearanceSettings(colourMode: 'system'));
    expect(previewing.modeFor(browserChose: true, browserMode: ThemeMode.dark), ThemeMode.system);
  });

  testWidgets('choosing a wallpaper previews it and fills in the accent colour from it', (tester) async {
    await pumpScreen(tester);
    final png = (await tester.runAsync(() => pngOf(const Color(0xFF001428), const Color(0xFF00AEEF))))!;
    (locator<FilePicker>() as _FakePicker).next = png;

    await tester.tap(find.byKey(const Key('appearance-wallpaper-choose')));
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300)));
    await tester.pump();

    expect(cubit.state.preview?.wallpaper, png);
    expect(cubit.state.preview?.accentHex, isNotNull);
    expect(find.text('Colour taken from the image. Change it above if you prefer.'), findsOneWidget);
    expect(find.byKey(const Key('appearance-wallpaper-remove')), findsOneWidget);

    await tester.tap(find.byKey(const Key('appearance-wallpaper-remove')));
    await tester.pump();
    expect(cubit.state.preview?.wallpaper, isNull);
  });

  testWidgets('a colour the company already chose is not replaced by picking a wallpaper', (tester) async {
    await pumpScreen(tester);
    await tester.enterText(find.byKey(const Key('appearance-accent')), '#7C3AED');
    await tester.pump();
    final png = (await tester.runAsync(() => pngOf(const Color(0xFF001428), const Color(0xFF00AEEF))))!;
    (locator<FilePicker>() as _FakePicker).next = png;

    await tester.tap(find.byKey(const Key('appearance-wallpaper-choose')));
    await tester.runAsync(() => Future<void>.delayed(const Duration(milliseconds: 300)));
    await tester.pump();

    expect(cubit.state.preview?.wallpaper, png);
    expect(cubit.state.preview?.accentHex, '#7C3AED');
  });

  testWidgets('a file that is not an image is refused with a reason and changes nothing', (tester) async {
    await pumpScreen(tester);
    (locator<FilePicker>() as _FakePicker).next = Uint8List.fromList([1, 2, 3, 4]);

    await tester.tap(find.byKey(const Key('appearance-wallpaper-choose')));
    await tester.pump();
    await tester.pump();

    expect(find.text('The image must be a PNG, JPEG or WebP file.'), findsOneWidget);
    expect(cubit.state.preview, isNull);
  });

  testWidgets('a server without the route says so, still previews, and cannot save', (tester) async {
    repository.readError = const ApiException('Not Found', statusCode: 404);
    await pumpScreen(tester);

    expect(cubit.state.unsupported, isTrue);
    expect(find.textContaining('does not support saving a colour or picture yet'), findsOneWidget);

    await tester.tap(find.byKey(const Key('appearance-preset-gold')));
    await tester.pump();
    expect(cubit.state.effective.presetKey, 'gold');
    expect(tester.widget<PrimaryButton>(find.byKey(const Key('appearance-save'))).onPressed, isNull);
  });

  testWidgets('a failure that might pass (server error, offline) is not taken for "not supported"', (tester) async {
    repository.readError = const ApiException('boom', statusCode: 500);
    await pumpScreen(tester);

    expect(cubit.state.unsupported, isFalse);
    expect(find.textContaining('does not support saving a colour or picture yet'), findsNothing);
  });

  testWidgets('a server that has the route is not marked unsupported', (tester) async {
    await pumpScreen(tester);
    expect(cubit.state.unsupported, isFalse);
  });

  testWidgets('a company logo and name preview, pick an animation, and save through the patching policy', (tester) async {
    final policy = FakePolicyRepository();
    cubit = fakeAppearanceCubit(repository: repository, policy: policy);
    await pumpScreen(tester);
    final png = (await tester.runAsync(() => pngOf(const Color(0xFF001428), const Color(0xFF00AEEF))))!;
    (locator<FilePicker>() as _FakePicker).next = png;

    expect(find.byKey(const Key('appearance-anim-fade')), findsNothing, reason: 'no logo, nothing to take turns with');

    await tester.enterText(find.byKey(const Key('appearance-org-name')), 'Example Corp');
    await tester.tap(find.byKey(const Key('appearance-logo-choose')));
    await tester.pump();
    await tester.pump();

    expect(cubit.state.organisationPreview, Organisation(name: 'Example Corp', logoPng: png));
    await tester.tap(find.byKey(const Key('appearance-anim-fade')));
    await tester.pump();
    expect(cubit.state.preview?.logoAnimation, 'fade');

    await tester.tap(find.byKey(const Key('appearance-save')));
    await tester.pumpAndSettle();

    expect(policy.saves.single.organisationName, 'Example Corp');
    expect(policy.saves.single.organisationLogoPng, png);
    // The policy itself goes back unchanged.
    expect(policy.saves.single.intervalValue, 7);
    expect(repository.saves.single.logoAnimation, 'fade');
    expect(cubit.state.organisation, Organisation(name: 'Example Corp', logoPng: png));
  });

  testWidgets('a logo that is not a PNG is refused with a reason', (tester) async {
    await pumpScreen(tester);
    (locator<FilePicker>() as _FakePicker).next = Uint8List.fromList([1, 2, 3, 4]);

    await tester.tap(find.byKey(const Key('appearance-logo-choose')));
    await tester.pump();
    await tester.pump();

    expect(find.text('The logo must be a PNG image.'), findsOneWidget);
    expect(cubit.state.organisationPreview, isNull);
  });

  testWidgets('on a server without the appearance route the name and logo can still be saved', (tester) async {
    repository.readError = const ApiException('Not Found', statusCode: 404);
    final policy = FakePolicyRepository();
    cubit = fakeAppearanceCubit(repository: repository, policy: policy);
    await pumpScreen(tester);

    await tester.enterText(find.byKey(const Key('appearance-org-name')), 'Example Corp');
    await tester.pump();
    expect(tester.widget<PrimaryButton>(find.byKey(const Key('appearance-save'))).onPressed, isNotNull);

    await tester.tap(find.byKey(const Key('appearance-save')));
    await tester.pumpAndSettle();
    expect(policy.saves.single.organisationName, 'Example Corp');
    expect(repository.saves, isEmpty);
  });

  group('the logo spot', () {
    Future<void> pumpSpot(WidgetTester tester, Organisation org, String animation) => tester.pumpWidget(
          MaterialApp(
            theme: AppTheme.light(),
            home: Scaffold(
              body: SizedBox(
                width: 200,
                child: LogoSpot(organisation: org, animation: animation, interval: const Duration(milliseconds: 500)),
              ),
            ),
          ),
        );

    testWidgets('starts on Kintsugi, then takes turns with the company, then back', (tester) async {
      final png = (await tester.runAsync(() => pngOf(const Color(0xFF001428), const Color(0xFF00AEEF))))!;
      for (final animation in ['spin', 'fade']) {
        await pumpSpot(tester, Organisation(name: 'Acme', logoPng: png), animation);
        expect(find.text('KINTSUGI'), findsOneWidget, reason: animation);

        await tester.pump(const Duration(milliseconds: 600));
        await tester.pump(const Duration(milliseconds: 1500));
        expect(find.text('ACME'), findsOneWidget, reason: animation);

        await tester.pump(const Duration(milliseconds: 500));
        await tester.pump(const Duration(milliseconds: 1500));
        expect(find.text('KINTSUGI'), findsOneWidget, reason: animation);
        await tester.pumpWidget(const SizedBox());
      }
    });

    testWidgets('stays on Kintsugi when animation is off or there is no logo', (tester) async {
      final png = (await tester.runAsync(() => pngOf(const Color(0xFF001428), const Color(0xFF00AEEF))))!;
      await pumpSpot(tester, Organisation(name: 'Acme', logoPng: png), 'off');
      await tester.pump(const Duration(seconds: 3));
      expect(find.text('KINTSUGI'), findsOneWidget);
      expect(find.text('ACME'), findsNothing);

      await pumpSpot(tester, const Organisation(name: 'Acme'), 'spin');
      await tester.pump(const Duration(seconds: 3));
      expect(find.text('KINTSUGI'), findsOneWidget);
      expect(find.text('Acme'), findsOneWidget, reason: 'a name with no logo is a quiet line under the title');
    });
  });
}
