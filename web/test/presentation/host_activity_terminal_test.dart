import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:go_router/go_router.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/host_activity_mapper.dart';
import 'package:kintsugi_web/domain/entities/host_activity.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_activity_usecases.dart';
import 'package:kintsugi_web/presentation/host_activity/host_activity_screen.dart';

class _Activity implements HostActivityRepository {
  @override
  Future<HostActivity> get(String hostId) async => hostActivityFromJson({
        'hostId': hostId,
        'hostname': 'alpha',
        'operatingSystem': 'Windows 11 Pro',
        'agentVersion': '0.12.5',
        'status': 'Online',
        'lastSeenUtc': '2026-11-05T01:00:00Z',
        'retentionMonths': 6,
        'events': [
          {
            'id': 'e1',
            'occurredUtc': '2026-11-05T02:00:00Z',
            'kind': 'AssignedPersonRevealed',
            'subject': 'alpha',
            'actor': 'admin@example.com',
            'count': 1,
          },
        ],
        'pendingUpdates': <Map<String, dynamic>>[],
        'openFailures': <Map<String, dynamic>>[],
      });

  @override
  Future<int> setRetention(int months) async => months;
}

void main() {
  tearDown(() => locator.reset());

  Future<void> pump(WidgetTester tester) async {
    final repo = _Activity();
    locator
      ..registerSingleton(GetHostActivity(repo))
      ..registerSingleton(SetHostActivityRetention(repo));

    final router = GoRouter(routes: [
      GoRoute(path: '/', builder: (_, _) => const Scaffold(body: HostActivityScreen(hostId: 'h1'))),
      GoRoute(
        path: '/hosts/:id/shell',
        builder: (_, s) => Scaffold(body: Text('SHELL ${s.pathParameters['id']} ${s.uri.queryParameters['hostname']}')),
      ),
      GoRoute(
        path: '/hosts/:id/remote',
        builder: (_, s) => Scaffold(body: Text('REMOTE ${s.pathParameters['id']} ${s.uri.queryParameters['hostname']}')),
      ),
    ]);

    tester.view.physicalSize = const Size(1500, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(MaterialApp.router(theme: AppTheme.light(), routerConfig: router));
    await tester.pumpAndSettle();
  }

  testWidgets('the header has Terminal and Connect, and Terminal opens this host\'s terminal', (tester) async {
    await pump(tester);

    expect(find.byKey(const Key('activity-terminal')), findsOneWidget);
    expect(find.byKey(const Key('activity-connect')), findsOneWidget);

    await tester.tap(find.byKey(const Key('activity-terminal')));
    await tester.pumpAndSettle();

    expect(find.text('SHELL h1 alpha'), findsOneWidget);
  });

  testWidgets('Connect opens remote control for this host', (tester) async {
    await pump(tester);

    await tester.tap(find.byKey(const Key('activity-connect')));
    await tester.pumpAndSettle();

    expect(find.text('REMOTE h1 alpha'), findsOneWidget);
  });

  testWidgets('the Terminal tab goes nowhere until its button is pressed', (tester) async {
    await pump(tester);

    // The header button reads the same; the tab comes after it in the tree.
    await tester.tap(find.text('TERMINAL').last);
    await tester.pumpAndSettle();

    // Showing the tab must not start a session: it is still the host page, with the button on offer.
    expect(find.textContaining('SHELL'), findsNothing);
    expect(find.byKey(const Key('terminal-tab-open')), findsOneWidget);

    await tester.tap(find.byKey(const Key('terminal-tab-open')));
    await tester.pumpAndSettle();
    expect(find.text('SHELL h1 alpha'), findsOneWidget);
  });

  testWidgets('the new timeline kind reads in plain words', (tester) async {
    await pump(tester);

    await tester.tap(find.text('ACTIVITY').first);
    await tester.pumpAndSettle();

    expect(find.text('Assigned person revealed'), findsOneWidget);
    expect(find.text('by admin@example.com'), findsOneWidget);
  });
}
