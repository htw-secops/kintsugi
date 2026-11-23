import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/presentation/remote_control/remote_shell_view.dart';
import 'package:xterm/xterm.dart';

/// A terminal has to be monospaced, and on web nothing enforces that but the family string this
/// widget asks for.
///
/// Flutter's canvas renderer has no system monospace to fall back to, so a family that is not
/// registered lands in the default *proportional* face. That shipped once, when the face came from
/// `google_fonts` under a family name of its own making and the terminal asked for the human one:
/// aligned output, box drawing and progress bars all wrong, and nothing in a build or an analyze
/// pass to say so. The face is now Liberation Mono, bundled under that name (pubspec.yaml).
void main() {
  testWidgets('the terminal asks for the family the app actually registers its monospace face under',
      (tester) async {
    await tester.pumpWidget(
      MaterialApp(
        theme: AppTheme.dark(),
        home: Scaffold(
          body: RemoteShellView(
            output: const Stream<Uint8List>.empty(),
            onInput: (_) {},
          ),
        ),
      ),
    );

    final terminal = tester.widget<TerminalView>(find.byType(TerminalView));

    expect(terminal.textStyle.fontFamily, AppTheme.monoFamily);
    // The failure this pins, spelled out: the human name is what a reader reaches for, and it is
    // the one string that cannot work.
    expect(terminal.textStyle.fontFamily, isNot('Share Tech Mono'));
  });
}
