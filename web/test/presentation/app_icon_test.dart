import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/applications/widgets/app_icon.dart';

void main() {
  test('a logo is chosen by whole word, so look-alike names get none', () {
    expect(AppIcon.assetFor('Google Chrome'), 'assets/img/apps/googlechrome.png');
    expect(AppIcon.assetFor('Docker Desktop'), 'assets/img/apps/docker.png');
    expect(AppIcon.assetFor('Git'), 'assets/img/apps/git.png');
    expect(AppIcon.assetFor('Notepad++'), 'assets/img/apps/notepadplusplus.png');
    expect(AppIcon.assetFor('Slackware Linux'), isNull);
    expect(AppIcon.assetFor('ZoomText'), isNull);
    expect(AppIcon.assetFor('GitHub Desktop'), isNull);
  });

  testWidgets('an application with no logo gets a tile with its first letter', (tester) async {
    await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: AppIcon('Zebra Tool'))));
    expect(find.text('Z'), findsOneWidget);
    expect(find.byType(Image), findsNothing);
  });

  testWidgets('an application with a logo shows the image', (tester) async {
    await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: AppIcon('Firefox'))));
    expect(find.byType(Image), findsOneWidget);
  });
}
