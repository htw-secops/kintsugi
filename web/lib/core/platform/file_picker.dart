import 'dart:typed_data';

/// The browser's "choose a file" dialog, behind an interface.
///
/// An interface for the same reason [PageNavigator] and [FileSaver] are: `package:web` is
/// unavailable under `flutter test`, so a screen that reached for the DOM directly could not be
/// pumped, and the Patching Policy screen's logo picker could not be tested at all.
abstract interface class FilePicker {
  /// Asks the person for one file and returns its bytes, or null if they cancelled.
  ///
  /// [accept] is an `<input accept>` list (`.png,image/png`). It only steers the dialog — a person
  /// can still choose "All files" — so the caller checks what comes back.
  ///
  /// A file larger than [maxBytes] throws [FileTooLargeException] without being read: reading is
  /// all or nothing, so a 500 MB file picked by mistake would otherwise be loaded whole only to be
  /// refused. The caller still checks the bytes it gets back.
  ///
  /// Must be called on the same turn as the click that asked for it, before any `await`: the
  /// browser opens the dialog only with transient user activation, and refuses silently without it.
  Future<Uint8List?> pick({required String accept, required int maxBytes});
}

/// The file chosen in [FilePicker.pick] was over its `maxBytes`, so none of it was read.
class FileTooLargeException implements Exception {
  const FileTooLargeException(this.size);

  final int size;

  @override
  String toString() => 'FileTooLargeException: $size bytes';
}
