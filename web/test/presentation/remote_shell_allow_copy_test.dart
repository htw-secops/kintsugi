import 'dart:async';
import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/gestures.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';
import 'package:kintsugi_web/presentation/remote_control/terminal_copy_setting.dart';
import 'package:shared_preferences/shared_preferences.dart';
import 'package:xterm/xterm.dart';

/// "Allow copy": selecting text in the remote terminal with the mouse copies it as soon as the button is
/// released, so an administrator does not have to know Ctrl+Shift+C (Ctrl+C is the interrupt on Windows and
/// Linux, so xterm cannot give it to the clipboard).
///
/// The arrangement is the one `main.dart` builds, every screen inside one `SelectionArea`, because that
/// region's gesture recognizer is what makes the release order matter: see `_takeKeyboard` in the widget.
void main() {
  Widget wrapped(Widget child, FocusNode selection) => MaterialApp(
        theme: AppTheme.dark(),
        home: Overlay.wrap(
          child: SelectionArea(
            focusNode: selection,
            child: Scaffold(body: child),
          ),
        ),
      );

  /// Pumps a terminal that has printed two lines, with a clipboard that records what is written to it.
  Future<List<String>> pumpTerminal(WidgetTester tester, {ValueListenable<bool>? allowCopy}) async {
    final copied = <String>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'Clipboard.setData') {
        copied.add((call.arguments as Map<Object?, Object?>)['text']! as String);
      }
      return null;
    });
    addTearDown(() => tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, null));

    final selection = FocusNode(skipTraversal: true, debugLabel: 'selection');
    addTearDown(selection.dispose);
    final output = StreamController<Uint8List>();
    addTearDown(output.close);

    await tester.pumpWidget(
      wrapped(RemoteShellView(output: output.stream, onInput: (_) {}, allowCopy: allowCopy), selection),
    );
    output.add(Uint8List.fromList(utf8.encode('alpha bravo\r\ncharlie delta\r\n')));
    await tester.pump();
    return copied;
  }

  /// A drag across both lines, held past the press timeout the way a real one is.
  Future<void> dragAcrossTwoLines(WidgetTester tester) async {
    final gesture = await tester.startGesture(
      tester.getTopLeft(find.byType(TerminalView)) + const Offset(2, 2),
      kind: PointerDeviceKind.mouse,
    );
    await tester.pump(const Duration(milliseconds: 150));
    await gesture.moveBy(const Offset(60, 30));
    await tester.pump();
    await gesture.up();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
  }

  testWidgets('with Allow copy on, letting go of a drag puts the selection on the clipboard and says so',
      (tester) async {
    final allow = ValueNotifier<bool>(true);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await dragAcrossTwoLines(tester);

    expect(copied, hasLength(1), reason: 'no key was pressed: the release alone should copy');
    expect(copied.single, contains('\n'), reason: 'a selection over two rows copies as two lines');
    expect(find.text('Copied (2 lines)'), findsOneWidget);

    // The note goes by itself.
    await tester.pump(const Duration(seconds: 3));
    expect(find.textContaining('Copied'), findsNothing);
    await tester.pumpAndSettle();
  });

  testWidgets('a word chosen by double-click is copied too, and the note says one line', (tester) async {
    final allow = ValueNotifier<bool>(true);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    final at = tester.getTopLeft(find.byType(TerminalView)) + const Offset(10, 8);
    await tester.tapAt(at, kind: PointerDeviceKind.mouse);
    await tester.pump(const Duration(milliseconds: 50));
    await tester.tapAt(at, kind: PointerDeviceKind.mouse);
    await tester.pump(const Duration(milliseconds: 100));

    expect(copied, isNotEmpty);
    expect(copied.last.trim(), 'alpha');
    expect(find.text('Copied (1 line)'), findsOneWidget);
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
  });

  testWidgets('with Allow copy off, the same drag leaves the clipboard alone', (tester) async {
    final allow = ValueNotifier<bool>(false);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await dragAcrossTwoLines(tester);

    expect(copied, isEmpty);
    expect(find.textContaining('Copied'), findsNothing);
    await tester.pumpAndSettle();
  });

  testWidgets('with no setting at all the terminal behaves as xterm does, and copies nothing on release',
      (tester) async {
    final copied = await pumpTerminal(tester);

    await dragAcrossTwoLines(tester);

    expect(copied, isEmpty);
    await tester.pumpAndSettle();
  });

  testWidgets('a plain click selects nothing, so it copies nothing', (tester) async {
    final allow = ValueNotifier<bool>(true);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await tester.tapAt(tester.getTopLeft(find.byType(TerminalView)) + const Offset(40, 40), kind: PointerDeviceKind.mouse);
    await tester.pump(const Duration(milliseconds: 400));

    expect(copied, isEmpty);
    expect(find.textContaining('Copied'), findsNothing);
    await tester.pumpAndSettle();
  });

  testWidgets('the switch is read at each release, so flipping it takes effect on the next selection',
      (tester) async {
    final allow = ValueNotifier<bool>(false);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await dragAcrossTwoLines(tester);
    expect(copied, isEmpty);

    allow.value = true;
    await dragAcrossTwoLines(tester);
    expect(copied, isNotEmpty);

    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
  });

  group('TerminalCopySetting', () {
    test('is on when nothing was ever stored, with or without preferences', () async {
      SharedPreferences.setMockInitialValues({});
      expect(TerminalCopySetting().value, isTrue);
      expect(TerminalCopySetting(await SharedPreferences.getInstance()).value, isTrue);
    });

    test('toggling flips it and keeps the choice in the browser for the next visit', () async {
      SharedPreferences.setMockInitialValues({});
      final preferences = await SharedPreferences.getInstance();
      final setting = TerminalCopySetting(preferences);

      await setting.toggle();

      expect(setting.value, isFalse);
      expect(preferences.getBool(TerminalCopySetting.storageKey), isFalse);
      expect(TerminalCopySetting(preferences).value, isFalse, reason: 'a new visit reads the stored choice');

      await setting.toggle();
      expect(preferences.getBool(TerminalCopySetting.storageKey), isTrue);
    });

    test('works for the life of the page when there are no preferences to keep it in', () async {
      final setting = TerminalCopySetting();

      await setting.toggle();

      expect(setting.value, isFalse);
    });
  });
}
