import 'package:flutter/foundation.dart';
import 'package:shared_preferences/shared_preferences.dart';

/// Whether letting go of the mouse over a selection in the remote terminal copies it.
///
/// **Off by default.** This is the operating system's clipboard, so anything highlighted in a root shell on
/// a managed host would reach clipboard history and clipboard sync (Windows clipboard history, macOS
/// Universal Clipboard), a token highlighted only to read it included. Windows Terminal ships its own
/// copy-on-select off for the same reason. An administrator who wants it turns it on once and the browser
/// remembers. Off, copying is Ctrl+Shift+C on Windows and Linux (Ctrl+C is the interrupt there, so xterm
/// cannot give it to the clipboard) and Cmd+C on a Mac.
///
/// Kept in the browser (`SharedPreferences`, which on the web is `localStorage`), like the theme: it is a
/// preference of the person at the keyboard, not of the host or the session. With no preferences to
/// keep it in, it still works for the life of the page.
class TerminalCopySetting extends ValueNotifier<bool> {
  TerminalCopySetting([this._preferences]) : super(_preferences?.getBool(storageKey) ?? false);

  static const storageKey = 'kintsugi-terminal-allow-copy';

  final SharedPreferences? _preferences;

  Future<void> toggle() async {
    value = !value;
    await _preferences?.setBool(storageKey, value);
  }
}
