import 'dart:typed_data';

/// A text file the administrator chose, already read.
class PickedTextFile {
  const PickedTextFile({required this.name, required this.text});

  final String name;
  final String text;
}

/// The browser's "choose a file" dialog, behind an interface.
///
/// An interface for the same reason [PageNavigator] and [FileSaver] are: `package:web` is
/// unavailable under `flutter test`, so a screen that reached for the DOM directly could not be
/// pumped, and neither the Patching Policy screen's logo picker nor the Hosts screen's Snipe-IT import
/// could be tested at all. The file is read **in the browser**; nothing is uploaded by choosing it.
/// What the screen then sends to the server is whatever the screen decides to send.
///
/// Both methods share the same rules:
///
/// * [accept] is an `<input accept>` list (`.png,image/png`). It only steers the dialog — a person
///   can still choose "All files" — so the caller checks what comes back.
/// * A file larger than `maxBytes` throws [FileTooLargeException] without being read: reading is
///   all or nothing, so a 500 MB file picked by mistake would otherwise be loaded whole only to be
///   refused.
/// * Must be called on the same turn as the click that asked for it, before any `await`: the
///   browser opens the dialog only with transient user activation, and refuses silently without it.
abstract interface class FilePicker {
  /// Asks the person for one file and returns its bytes, or null if they cancelled.
  Future<Uint8List?> pick({required String accept, required int maxBytes});

  /// Asks the person for one file and returns its name and its text decoded as UTF-8, or null if
  /// they cancelled.
  Future<PickedTextFile?> pickText({required int maxBytes, String accept = '.csv,text/csv'});
}

/// The file chosen in [FilePicker.pick] or [FilePicker.pickText] was over its `maxBytes`, so none of
/// it was read.
class FileTooLargeException implements Exception {
  const FileTooLargeException(this.size);

  final int size;

  @override
  String toString() => 'FileTooLargeException: $size bytes';
}
