import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/core/theme/kintsugi_palette.dart';
import 'package:kintsugi_web/core/theme/theme_cubit.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/server_info_usecases.dart';
import 'package:kintsugi_web/presentation/shell/app_shell.dart';
import 'package:shared_preferences/shared_preferences.dart';

import 'session_bloc_test.dart'
    show FakeSessionRepository, blocFor, readySession;

class _FakeServerInfoRepository implements ServerInfoRepository {
  @override
  Future<String> version() => Completer<String>().future;
}

/// WCAG contrast ratio of two opaque colours: 4.5 is the floor for small text.
double _contrast(Color a, Color b) {
  double lum(Color c) {
    double ch(double v) => v <= 0.03928
        ? v / 12.92
        : math.pow((v + 0.055) / 1.055, 2.4).toDouble();
    return 0.2126 * ch(c.r) + 0.7152 * ch(c.g) + 0.0722 * ch(c.b);
  }

  final hi = math.max(lum(a), lum(b));
  final lo = math.min(lum(a), lum(b));
  return (hi + 0.05) / (lo + 0.05);
}

/// The sidebar order is a product decision, and the light theme's small text has to be readable.
void main() {
  setUp(() {
    SharedPreferences.setMockInitialValues({});
    locator.registerSingleton(GetServerVersion(_FakeServerInfoRepository()));
  });

  tearDown(() => locator.reset());

  Future<void> pumpShell(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1400, 1000);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final preferences = await SharedPreferences.getInstance();
    await tester.pumpWidget(
      MultiBlocProvider(
        providers: [
          BlocProvider.value(
            value: blocFor(FakeSessionRepository(session: readySession())),
          ),
          BlocProvider(create: (_) => ThemeCubit(preferences)),
        ],
        child: MaterialApp(
          theme: AppTheme.light(),
          home: const AppShell(location: Routes.hosts, child: SizedBox()),
        ),
      ),
    );
    await tester.pump();
  }

  testWidgets('the sidebar lists the menus in the agreed order', (
    tester,
  ) async {
    await pumpShell(tester);

    final labels = [
      'Overview',
      'Autonomy',
      'Hosts',
      'Patch Health',
      'Applications',
      'Vulnerabilities',
      'Baselines',
      'Sync',
      'Settings',
    ];
    final ys = [
      for (final label in labels) tester.getTopLeft(find.text(label)).dy,
    ];
    expect(ys, orderedEquals([...ys]..sort()));
  });

  test('light small text meets the 4.5:1 contrast floor on the page and on a panel', () {
    const p = KintsugiPalette.light;
    for (final (name, color) in [
      ('muted', p.muted),
      ('text', p.text),
    ]) {
      expect(
        _contrast(color, p.background),
        greaterThanOrEqualTo(4.5),
        reason: '$name on background',
      );
      expect(
        _contrast(color, p.surface),
        greaterThanOrEqualTo(4.5),
        reason: '$name on white',
      );
    }
  });
}
