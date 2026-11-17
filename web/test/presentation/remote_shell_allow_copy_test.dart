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
  Future<List<String>> pumpTerminal(WidgetTester tester,
      {ValueListenable<bool>? allowCopy, bool failWrite = false}) async {
    final copied = <String>[];
    tester.binding.defaultBinaryMessenger.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'Clipboard.setData') {
        if (failWrite) throw PlatformException(code: 'denied');
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

  /// A mouse press that drifts [dx] pixels before the button is let go, with [buttons] held. 3px is past
  /// xterm's 2px pan slop and well inside the 18px a tap tolerates: a click that is really a tiny drag.
  Future<void> press(WidgetTester tester, Offset at, {double dx = 0, int buttons = kPrimaryMouseButton}) async {
    final gesture = await tester.startGesture(
      tester.getTopLeft(find.byType(TerminalView)) + at,
      kind: PointerDeviceKind.mouse,
      buttons: buttons,
    );
    await tester.pump(const Duration(milliseconds: 150));
    if (dx != 0) await gesture.moveBy(Offset(dx, 0));
    await tester.pump();
    await gesture.up();
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 50));
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

  testWidgets('the switch is read at each release, so flipping it takes effect on the next selection, not the one already open',
      (tester) async {
    final allow = ValueNotifier<bool>(false);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await dragAcrossTwoLines(tester);
    expect(copied, isEmpty);

    allow.value = true;
    expect(copied, isEmpty, reason: 'switching on copies nothing by itself, the selection already open stays put');

    // A new selection, from somewhere else: the one open before was never copied and is not copied now.
    await press(tester, const Offset(2, 2), dx: 60);
    expect(copied, isNotEmpty);

    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
  });

  testWidgets('a click that drifts a few pixels is a drag of one cell, and copies nothing', (tester) async {
    final allow = ValueNotifier<bool>(true);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await press(tester, const Offset(12, 8), dx: 3);

    expect(copied, isEmpty, reason: 'clicking into the terminal must not put one character on the clipboard');
    expect(find.textContaining('Copied'), findsNothing);
    await tester.pumpAndSettle();
  });

  testWidgets('a right-click leaves the clipboard alone, though a selection is still on screen', (tester) async {
    final allow = ValueNotifier<bool>(true);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await dragAcrossTwoLines(tester);
    expect(copied, hasLength(1));
    await tester.pump(const Duration(seconds: 3));

    await press(tester, const Offset(40, 40), buttons: kSecondaryMouseButton);
    await press(tester, const Offset(40, 40), buttons: kMiddleMouseButton);

    expect(copied, hasLength(1), reason: 'the old selection must not replace what was copied since');
    await tester.pumpAndSettle();
  });

  testWidgets('dragging out the very same selection again does not copy it a second time', (tester) async {
    final allow = ValueNotifier<bool>(true);
    final copied = await pumpTerminal(tester, allowCopy: allow);

    await dragAcrossTwoLines(tester);
    expect(copied, hasLength(1));
    await tester.pump(const Duration(seconds: 3));

    // The first selection is still on screen when this press begins, and the press ends on the same range.
    await dragAcrossTwoLines(tester);
    expect(copied, hasLength(1), reason: 'nothing changed, so there is nothing new to put on the clipboard');
    await tester.pumpAndSettle();
  });

  testWidgets('when the browser refuses the write, the note says so instead of claiming a copy',
      (tester) async {
    final allow = ValueNotifier<bool>(true);
    await pumpTerminal(tester, allowCopy: allow, failWrite: true);

    await dragAcrossTwoLines(tester);

    expect(find.text('Copy failed'), findsOneWidget);
    expect(find.textContaining('Copied'), findsNothing);
    await tester.pump(const Duration(seconds: 3));
    await tester.pumpAndSettle();
  });

  group('TerminalCopySetting', () {
    test('is off when nothing was ever stored, with or without preferences', () async {
      SharedPreferences.setMockInitialValues({});
      expect(TerminalCopySetting().value, isFalse);
      expect(TerminalCopySetting(await SharedPreferences.getInstance()).value, isFalse);
    });

    test('toggling flips it and keeps the choice in the browser for the next visit', () async {
      SharedPreferences.setMockInitialValues({});
      final preferences = await SharedPreferences.getInstance();
      final setting = TerminalCopySetting(preferences);

      await setting.toggle();

      expect(setting.value, isTrue);
      expect(preferences.getBool(TerminalCopySetting.storageKey), isTrue);
      expect(TerminalCopySetting(preferences).value, isTrue, reason: 'a new visit reads the stored choice');

      await setting.toggle();
      expect(preferences.getBool(TerminalCopySetting.storageKey), isFalse);
    });

    test('works for the life of the page when there are no preferences to keep it in', () async {
      final setting = TerminalCopySetting();

      await setting.toggle();

      expect(setting.value, isTrue);
    });
  });
}
