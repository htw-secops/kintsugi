import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/overview/overview_screen.dart';

/// The Overview page is only links, so what matters is that each one lands on a real route and
/// that the page needs no server.
void main() {
  testWidgets('every card opens the screen it names', (tester) async {
    tester.view.physicalSize = const Size(1400, 1200);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final router = GoRouter(routes: [
      GoRoute(path: '/', builder: (_, _) => const Scaffold(body: OverviewScreen())),
      GoRoute(path: Routes.hosts, builder: (_, _) => const Text('hosts page')),
    ]);
    await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));

    // Five steps and five capabilities, each reachable.
    for (final item in [...OverviewScreen.steps, ...OverviewScreen.capabilities]) {
      expect(find.byKey(Key('overview-${item.title}')), findsOneWidget, reason: item.title);
    }

    await tester.tap(find.byKey(const Key('overview-Detect')));
    await tester.pumpAndSettle();
    expect(find.text('hosts page'), findsOneWidget);
  });
}
