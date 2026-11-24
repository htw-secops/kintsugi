import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/router/app_router.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/overview/overview_art.dart';
import 'package:kintsugi_web/presentation/overview/overview_screen.dart';

/// The Overview page is cards that open, and a button in each that goes to a real screen.
void main() {
  Future<void> pumpPage(
    WidgetTester tester, {
    double width = 1400,
    ThemeData? theme,
  }) async {
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
      MaterialApp.router(
        theme: theme ?? AppTheme.light(),
        routerConfig: router,
      ),
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

  // The art is drawn in code, so what can go wrong is layout and a paint error in one theme or one width.
  for (final dark in [false, true]) {
    for (final entry in {1400.0: 1, 900.0: 2, 600.0: 3, 400.0: 5}.entries) {
      testWidgets(
        'the network and logos draw in ${dark ? 'dark' : 'light'} at ${entry.key.toInt()}px',
        (tester) async {
          await pumpPage(
            tester,
            width: entry.key,
            theme: dark ? AppTheme.dark() : AppTheme.light(),
          );
          await tester.pump(const Duration(seconds: 1));

          // One network per row of flow cards, none for the capabilities.
          expect(find.byType(OverviewNetwork), findsNWidgets(entry.value));
          // Five capability cards, five logos; the flow has none.
          expect(find.byType(OverviewLogo), findsNWidgets(5));
          for (final item in OverviewScreen.steps) {
            expect(
              find.descendant(
                of: card(item.title),
                matching: find.byType(OverviewLogo),
              ),
              findsNothing,
            );
          }
          // A network sits right on top of its first card, so the line it drops lands in the card.
          final first = tester.getRect(find.byType(OverviewNetwork).first);
          expect(
            first.bottom,
            moreOrLessEquals(tester.getRect(card('Detect')).top, epsilon: 1.0),
          );
          expect(tester.takeException(), isNull);
        },
      );
    }
  }

  testWidgets(
    'a capability card with its logo opens and shows its button below the logo',
    (tester) async {
      await pumpPage(tester, theme: AppTheme.dark());

      await tester.tap(card('Vulnerabilities'));
      await tester.pump();
      final logo = tester.getRect(
        find.descendant(
          of: card('Vulnerabilities'),
          matching: find.byType(OverviewLogo),
        ),
      );
      final button = tester.getRect(
        find.byKey(const Key('overview-open-Vulnerabilities')),
      );
      expect(button.bottom, lessThanOrEqualTo(logo.top + 0.5));
      expect(tester.takeException(), isNull);
    },
  );
}
