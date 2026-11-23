import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/platform/file_picker.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/core/widgets/buttons.dart';
import 'package:kintsugi_web/domain/entities/appearance.dart';
import 'package:kintsugi_web/presentation/settings/appearance_cubit.dart';
import 'package:kintsugi_web/presentation/settings/appearance_screen.dart';
import 'package:shared_preferences/shared_preferences.dart';

import '../core/image_accent_test.dart' show pngOf;
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

  testWidgets('choosing a theme and a mode previews at once and saves only on Save', (tester) async {
    await pumpScreen(tester);
    expect(find.text('Example Corp'), findsOneWidget);

    await tester.tap(find.byKey(const Key('appearance-preset-gold')));
    await tester.tap(find.byKey(const Key('appearance-mode-light')));
    await tester.pump();

    expect(cubit.state.preview, const AppearanceSettings(colourMode: 'light', presetKey: 'gold'));
    expect(repository.saves, isEmpty);

    await tester.tap(find.byKey(const Key('appearance-save')));
    await tester.pumpAndSettle();

    expect(repository.saves.single, const AppearanceSettings(colourMode: 'light', presetKey: 'gold'));
    expect(cubit.state.saved.presetKey, 'gold');
    expect(cubit.state.preview, isNull);
    expect(find.text('Settings saved.'), findsOneWidget);
  });

  testWidgets('an invalid colour is explained and cannot be saved; a valid one is normalised', (tester) async {
    await pumpScreen(tester);
    await tester.tap(find.byKey(const Key('appearance-mode-light')));
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
    expect(find.text('Accent colour taken from the image. Change it below if you prefer.'), findsOneWidget);
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
}
