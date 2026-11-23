import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/domain/entities/application.dart';
import 'package:kintsugi_web/presentation/applications/applications_bloc.dart';
import 'package:kintsugi_web/presentation/applications/applications_tiles.dart';

import 'application_filters_test.dart' show path;

/// The tiles above the Applications table. What matters is that a tile's number is the number of
/// rows its click would list, and that pressing the chosen tile again hands back the same key.
void main() {
  ApplicationRow app(String name, String statusKey, {List<String> behind = const []}) =>
      ApplicationRow(
        name: name,
        hostCount: 2,
        hostNames: const ['alpha', 'beta'],
        upgradePaths: [path(statusKey: statusKey, hostNamesNeedingUpdate: behind)],
        children: const [],
      );

  final state = ApplicationsState(
    loading: false,
    overview: ApplicationOverview(
      applications: [
        app('A', 'update-available', behind: ['alpha']),
        app('B', 'update-available', behind: ['beta']),
        // Unsigned and behind: wears "Review And Sign" but is also an update available.
        app('C', 'review-sign', behind: ['alpha']),
        app('D', 'check-failed'),
        app('E', 'up-to-date'),
      ],
      totalApplicationCount: 5,
      allHostNames: const ['alpha', 'beta'],
    ),
  );

  test('statusCount counts what the filter would list', () {
    expect(state.statusCount('update-available'), 3);
    expect(state.statusCount('review-sign'), 1);
    expect(state.statusCount('check-failed'), 1);
    expect(state.statusCount('not-found'), 0);
  });

  testWidgets('a tile shows its count and reports its key when pressed', (tester) async {
    final pressed = <String>[];
    await tester.pumpWidget(MaterialApp(
      theme: AppTheme.light(),
      home: Scaffold(body: ApplicationsTiles(state: state, onStatus: pressed.add)),
    ));

    expect(find.text('3'), findsOneWidget);
    await tester.tap(find.byKey(const Key('status-tile-check-failed')));
    expect(pressed, ['check-failed']);
  });
}
