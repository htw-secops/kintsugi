import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/overview/overview_screen.dart';

/// The Overview page is cards that open, and a button in each that goes to a real screen.
void main() {
  Future<void> pumpPage(WidgetTester tester, {double width = 1400}) async {
    tester.view.physicalSize = Size(width, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    final router = GoRouter(
      routes: [
        GoRoute(
          path: '/',
          builder: (_, _) => const Scaffold(body: OverviewScreen()),
        ),
        GoRoute(
          path: Routes.hosts,
          builder: (_, _) => const Text('hosts page'),
        ),
      ],
    );
    await tester.pumpWidget(
      MaterialApp.router(theme: AppTheme.light(), routerConfig: router),
    );
  }

  Finder card(String title) => find.byKey(Key('overview-$title'));

  testWidgets('every closed card has one width, five steps to a row', (
    tester,
  ) async {
    await pumpPage(tester);

    final all = [...OverviewScreen.steps, ...OverviewScreen.capabilities];
    final rects = [for (final item in all) tester.getRect(card(item.title))];
    for (final rect in rects) {
      expect(rect.width, moreOrLessEquals(rects.first.width, epsilon: 0.5));
      // A floor only: the runner resolves no web font, so its text is far wider than the
      // browser's and a card can outgrow the shared height here. Equal height is checked by eye.
      expect(rect.height, greaterThanOrEqualTo(232));
    }
    final stepTops = [
      for (final item in OverviewScreen.steps)
        tester.getRect(card(item.title)).top,
    ];
    expect(stepTops.every((top) => (top - stepTops.first).abs() < 0.5), isTrue);
  });

  testWidgets(
    'the arrow opens one card; the cards beside it keep their height',
    (tester) async {
      await pumpPage(tester);

      final closed = tester.getSize(card('Detect')).height;
      final beside = tester.getSize(card('Research')).height;
      expect(find.byKey(const Key('overview-open-Detect')), findsNothing);

      await tester.tap(card('Detect'));
      await tester.pumpAndSettle();

      expect(find.byKey(const Key('overview-open-Detect')), findsOneWidget);
      expect(tester.getSize(card('Detect')).height, greaterThan(closed));
      expect(
        tester.getSize(card('Research')).height,
        moreOrLessEquals(beside, epsilon: 0.5),
      );

      await tester.tap(card('Detect'));
      await tester.pumpAndSettle();
      expect(find.byKey(const Key('overview-open-Detect')), findsNothing);
      expect(
        tester.getSize(card('Detect')).height,
        moreOrLessEquals(closed, epsilon: 0.5),
      );
    },
  );

  testWidgets('the button in an open card opens the screen it names', (
    tester,
  ) async {
    await pumpPage(tester);

    await tester.tap(card('Detect'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const Key('overview-open-Detect')));
    await tester.pumpAndSettle();
    expect(find.text('hosts page'), findsOneWidget);
  });

  testWidgets(
    'a narrow window folds the cards into fewer columns without overflow',
    (tester) async {
      await pumpPage(tester, width: 600);
      expect(tester.takeException(), isNull);
      final first = tester.getRect(card('Detect'));
      final third = tester.getRect(card('Review and sign'));
      expect(third.top, greaterThan(first.top));
    },
  );
}
