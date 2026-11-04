import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/diagnostics/diagnostics_log.dart';
import 'package:kintsugi_web/core/platform/file_downloader.dart';
import 'package:kintsugi_web/core/platform/page_navigator.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/diagnostics/diagnostics_screen.dart';

class _FakeDownloader implements FileDownloader {
  final saved = <({String name, String mime, String content})>[];

  @override
  void save(String filename, String mimeType, String content) =>
      saved.add((name: filename, mime: mimeType, content: content));
}

class _FakeNavigator implements PageNavigator {
  final openedInNewTab = <String>[];

  @override
  void go(String url) {}

  @override
  void post(String url, {Map<String, String> fields = const {}}) {}

  @override
  void openInNewTab(String url) => openedInNewTab.add(url);
}

/// The Diagnostics page: finding one line in a long run, getting it out of the app, and not losing
/// the runs when nothing is open.
void main() {
  late DiagnosticsLog log;
  late _FakeDownloader downloader;
  late _FakeNavigator navigator;
  late List<String> clipboard;

  setUp(() {
    log = DiagnosticsLog();
    downloader = _FakeDownloader();
    navigator = _FakeNavigator();
    clipboard = [];
    locator
      ..registerSingleton<FileDownloader>(downloader)
      ..registerSingleton<PageNavigator>(navigator);
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger.setMockMethodCallHandler(
      SystemChannels.platform,
      (call) async {
        if (call.method == 'Clipboard.setData') {
          clipboard.add((call.arguments as Map)['text'] as String);
        }
        return null;
      },
    );
  });

  tearDown(() {
    locator.reset();
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(SystemChannels.platform, null);
  });

  /// Two runs, the older one first: a Check for Updates that failed on two rows, then a clean
  /// vulnerability assessment.
  void recordTwoRuns() {
    log
      ..record(
        title: 'Check for Updates',
        kind: DiagnosticsKind.error,
        summary: '1 updated, 2 failed.',
        source: 'Applications',
        lines: const [
          DiagnosticsLine('Slack (macOS): the script did not report a version.'),
          DiagnosticsLine('Zoom (macOS): the AI agent returned no script.'),
        ],
      )
      ..record(
        title: 'Vulnerability assessment',
        kind: DiagnosticsKind.success,
        summary: 'Checked 412 installed version(s).',
        source: 'Settings > Vulnerabilities',
      );
  }

  Future<void> pumpScreen(WidgetTester tester) async {
    tester.view.physicalSize = const Size(1500, 1600);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: DiagnosticsLogScope(log: log, child: const DiagnosticsScreen()),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('says so, and offers no export, when nothing has been recorded', (tester) async {
    await pumpScreen(tester);

    expect(find.textContaining('Nothing has been recorded yet'), findsOneWidget);
    expect(tester.widget<OutlinedButton>(find.widgetWithText(OutlinedButton, 'COPY ALL')).onPressed, isNull);
    expect(tester.widget<OutlinedButton>(find.widgetWithText(OutlinedButton, 'CLEAR…')).onPressed, isNull);
  });

  testWidgets('lists the runs newest first, and opens the newest', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    final newest = tester.getTopLeft(find.byKey(ValueKey('run-${log.entries.first.id}')));
    final oldest = tester.getTopLeft(find.byKey(ValueKey('run-${log.entries.last.id}')));
    expect(newest.dy, lessThan(oldest.dy));
    expect(find.text('VULNERABILITY ASSESSMENT'), findsOneWidget);
  });

  testWidgets('shows a run\'s lines when its entry is chosen', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
    await tester.pumpAndSettle();

    expect(find.text('CHECK FOR UPDATES'), findsOneWidget);
    expect(find.textContaining('Slack (macOS)', findRichText: true), findsOneWidget);
    expect(find.textContaining('Zoom (macOS)', findRichText: true), findsOneWidget);
  });

  testWidgets('the kind filter narrows the list of runs', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    await tester.tap(find.text('NEEDS ATTENTION'));
    await tester.pumpAndSettle();

    expect(find.byKey(ValueKey('run-${log.entries.last.id}')), findsOneWidget);
    expect(find.byKey(ValueKey('run-${log.entries.first.id}')), findsNothing);
  });

  group('search', () {
    testWidgets('narrows the runs to those that match, and the lines to the matching ones', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'zoom');
      await tester.pumpAndSettle();

      expect(find.byKey(ValueKey('run-${log.entries.first.id}')), findsNothing);
      expect(find.textContaining('Zoom (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Slack (macOS)', findRichText: true), findsNothing);
    });

    testWidgets('shows the whole run when the match is in its title', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'check for');
      await tester.pumpAndSettle();

      expect(find.textContaining('Slack (macOS)', findRichText: true), findsOneWidget);
      expect(find.textContaining('Zoom (macOS)', findRichText: true), findsOneWidget);
    });

    testWidgets('says when nothing matches', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.enterText(find.byType(TextField), 'no such thing');
      await tester.pumpAndSettle();

      expect(find.text('No run matches.'), findsWidgets);
    });
  });

  group('copying', () {
    testWidgets('copies one run as text', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      await tester.tap(find.text('COPY THIS RUN'));
      await tester.pumpAndSettle();

      expect(clipboard, hasLength(1));
      expect(clipboard.single, contains('Check for Updates'));
      expect(clipboard.single, contains('Slack (macOS): the script did not report a version.'));
      expect(clipboard.single, isNot(contains('Vulnerability assessment')));
      expect(find.text('Copied this run.'), findsOneWidget);
    });

    testWidgets('copies every listed run with Copy all', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.tap(find.text('COPY ALL'));
      await tester.pumpAndSettle();

      expect(clipboard.single, contains('Check for Updates'));
      expect(clipboard.single, contains('Vulnerability assessment'));
    });

    testWidgets('Copy all copies only what the filter and search leave listed', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      // "OK" is also the status chip of the run that opens by default; the filter chip is the first.
      await tester.tap(find.text('OK').first);
      await tester.pumpAndSettle();

      await tester.tap(find.text('COPY ALL'));
      await tester.pumpAndSettle();

      expect(clipboard.single, contains('Vulnerability assessment'));
      expect(clipboard.single, isNot(contains('Check for Updates')));
    });

    testWidgets('copies one line from its own button', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      final line = find.byKey(ValueKey('line-${log.entries.last.id}-1'));
      final pointer = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await pointer.addPointer(location: Offset.zero);
      addTearDown(pointer.removePointer);
      await pointer.moveTo(tester.getCenter(line));
      await tester.pumpAndSettle();
      await tester.tap(find.descendant(of: line, matching: find.byIcon(Icons.copy_outlined)));
      await tester.pumpAndSettle();

      expect(clipboard.single, 'Zoom (macOS): the AI agent returned no script.');
    });
  });

  group('exporting', () {
    Future<void> choose(WidgetTester tester, String item) async {
      await tester.tap(find.text('EXPORT'));
      await tester.pumpAndSettle();
      await tester.tap(find.text(item));
      await tester.pumpAndSettle();
    }

    testWidgets('exports the open run as CSV', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.tap(find.byKey(ValueKey('run-${log.entries.last.id}')));
      await tester.pumpAndSettle();

      await choose(tester, 'This run as CSV');

      final file = downloader.saved.single;
      expect(file.name, 'diagnostics-run.csv');
      expect(file.mime, 'text/csv');
      expect(file.content, contains('Slack (macOS): the script did not report a version.'));
      expect(file.content, isNot(contains('Vulnerability assessment')));
      expect(find.text('Exported diagnostics-run.csv.'), findsOneWidget);
    });

    testWidgets('exports every saved run as JSON', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await choose(tester, 'All runs as JSON');

      final file = downloader.saved.single;
      expect(file.name, 'diagnostics-all.json');
      expect(file.mime, 'application/json');
      expect(file.content, contains('Check for Updates'));
      expect(file.content, contains('Vulnerability assessment'));
    });

    testWidgets('an export of all runs is not narrowed by the search', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);
      await tester.enterText(find.byType(TextField), 'zoom');
      await tester.pumpAndSettle();

      await choose(tester, 'All runs as CSV');

      expect(downloader.saved.single.content, contains('Vulnerability assessment'));
    });
  });

  testWidgets('Open in new tab asks the browser for this page', (tester) async {
    recordTwoRuns();
    await pumpScreen(tester);

    await tester.tap(find.text('OPEN IN NEW TAB'));

    expect(navigator.openedInNewTab, ['/diagnostics']);
  });

  group('clearing', () {
    testWidgets('asks first, and removes everything once confirmed', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.tap(find.text('CLEAR…'));
      await tester.pumpAndSettle();
      expect(find.text('Clear every saved run?'), findsOneWidget);
      expect(log.count, 2);

      await tester.tap(find.text('CLEAR'));
      await tester.pumpAndSettle();

      expect(log.isEmpty, isTrue);
      expect(find.textContaining('Nothing has been recorded yet'), findsOneWidget);
    });

    testWidgets('keeps everything when cancelled', (tester) async {
      recordTwoRuns();
      await pumpScreen(tester);

      await tester.tap(find.text('CLEAR…'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('CANCEL'));
      await tester.pumpAndSettle();

      expect(log.count, 2);
    });
  });

  testWidgets('shows a run another tab records while it is open', (tester) async {
    await pumpScreen(tester);
    expect(find.textContaining('Nothing has been recorded yet'), findsOneWidget);

    log.record(title: 'Check for Updates', kind: DiagnosticsKind.error, summary: 'x', lines: const [DiagnosticsLine('a')]);
    await tester.pumpAndSettle();

    expect(find.text('CHECK FOR UPDATES'), findsOneWidget);
  });
}
