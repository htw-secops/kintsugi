import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Whether letting go of the mouse over a selection in the remote terminal copies it.
///
/// **On by default.** Selecting text in a terminal in order to copy it is nearly always the point, and
/// without this the only way to copy on Windows or Linux is Ctrl+Shift+C: Ctrl+C is the interrupt
/// there, so xterm cannot give it to the clipboard. Turning it off puts the terminal back to that
/// behaviour, for an administrator who selects text only to read it and does not want the clipboard
/// replaced under them.
///
/// Kept in the browser (`SharedPreferences`, which on the web is `localStorage`), like the theme: it is a
/// preference of the person at the keyboard, not of the host or the session. With no preferences to
/// keep it in, it still works for the life of the page.
class TerminalCopySetting extends ValueNotifier<bool> {
  TerminalCopySetting([this._preferences]) : super(_preferences?.getBool(storageKey) ?? true);

  static const storageKey = 'kintsugi-terminal-allow-copy';

  final SharedPreferences? _preferences;

  Future<void> toggle() async {
    value = !value;
    await _preferences?.setBool(storageKey, value);
  }
}
