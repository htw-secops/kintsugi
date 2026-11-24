import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/platform/file_picker.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/presentation/settings/appearance_screen.dart';
import 'package:kintsugi_web/presentation/shell/app_shell.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'dart:typed_data';

import '../support/appearance_fakes.dart';
import 'session_bloc_test.dart' show FakeSessionRepository, blocFor, readySession;

class _Version implements ServerInfoRepository {
  @override
  Future<String> version() async => '1.0.0';
}

class _NoPicker implements FilePicker {
  @override
  Future<Uint8List?> pick({required String accept, required int maxBytes}) async => null;

  @override
  Future<PickedTextFile?> pickText({required int maxBytes, String accept = ''}) async => null;
}

/// An administrator in light mode who saves anything on Appearance, even only a colour, must stay in light mode.
/// Saving used to clear the browser's pick without anything rebuilding, so the next start was dark and the sidebar
/// button still said "Dark mode", whose first click did nothing visible.
void main() {
  setUp(() => SharedPreferences.setMockInitialValues({}));
  tearDown(() => locator.reset());

  testWidgets('saving in light mode keeps light mode, and the sidebar button still offers Dark mode', (tester) async {
    locator
      ..registerSingleton(GetServerVersion(_Version()))
      ..registerSingleton<FilePicker>(_NoPicker());
    tester.view.physicalSize = const Size(1500, 1800);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final preferences = await SharedPreferences.getInstance();
    final theme = ThemeCubit(preferences);
    await theme.toggle(); // the browser picked light
    expect(theme.state, ThemeMode.light);
    final repository = FakeAppearanceRepository();
    final appearance = fakeAppearanceCubit(repository: repository);
    await appearance.load();

    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider.value(value: blocFor(FakeSessionRepository(session: readySession()))),
          BlocProvider.value(value: theme),
          BlocProvider.value(value: appearance),
        ],
        child: BlocBuilder<ThemeCubit, ThemeMode>(
          builder: (context, mode) => MaterialApp(
            theme: AppTheme.light(),
            darkTheme: AppTheme.dark(),
            themeMode: mode,
            home: const AppShell(location: '/settings/appearance', child: Scaffold(body: AppearanceSettingsScreen())),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Dark mode'), findsOneWidget);

    await tester.tap(find.byKey(const Key('appearance-preset-gold')));
    await tester.pump();
    await tester.tap(find.byKey(const Key('appearance-save')));
    await tester.pumpAndSettle();

    expect(repository.saves, hasLength(1));
    expect(theme.state, ThemeMode.light);
    expect(theme.chosen, isTrue, reason: "the browser's own pick is still stored");
    expect(find.text('Dark mode'), findsOneWidget);
    expect(find.text('Light mode'), findsNothing);
  });
}
