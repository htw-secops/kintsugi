import 'dart:async';
import 'dart:js_interop';
import 'dart:typed_data';

import 'package:web/web.dart' as web;

import 'file_picker.dart';

/// The real [FilePicker]: a hidden `<input type="file">`, clicked once and thrown away. One of the
/// few files in this app that touch the DOM. Nothing is uploaded here — the file is read in the
/// browser and the caller decides what, if anything, travels to the server.
class BrowserFilePicker implements FilePicker {
  const BrowserFilePicker();

  @override
  Future<Uint8List?> pick({required String accept, required int maxBytes}) => _pickFile<Uint8List>(
        accept,
        maxBytes,
        (file) async => (await file.arrayBuffer().toDart).toDart.asUint8List(),
      );

  /// Read with `Blob.text()`, which decodes UTF-8 the way the browser does (a leading byte-order
  /// mark dropped, a malformed sequence replaced rather than thrown), as a spreadsheet's CSV export
  /// needs.
  @override
  Future<PickedTextFile?> pickText({required int maxBytes, String accept = '.csv,text/csv'}) =>
      _pickFile<PickedTextFile>(
        accept,
        maxBytes,
        (file) async => PickedTextFile(name: file.name, text: (await file.text().toDart).toDart),
      );

  /// Opens the dialog, waits for a choice, refuses an oversized file before any of it is read, and
  /// otherwise hands the file to [read]. Everything before the first `await` runs synchronously from
  /// the caller's click, which is what keeps the dialog inside the user's gesture.
  Future<T?> _pickFile<T>(String accept, int maxBytes, Future<T> Function(web.File file) read) async {
    final input = web.HTMLInputElement()
      ..type = 'file'
      ..accept = accept
      ..style.display = 'none';
    final chosen = Completer<web.File?>();

    // **Not `async`, and that is not style.** `toJS` only accepts a function that returns `void`: an
    // `async` closure returns a `Future<Null>`, and `flutter build web` refuses it ("Function converted
    // via 'toJS' contains invalid types"). The widget tests run on the Dart VM, which never compiles
    // for the browser, so nothing but a real web build can see this.
    input.addEventListener(
      'change',
      ((web.Event _) {
        final files = input.files;
        final file = files != null && files.length > 0 ? files.item(0) : null;
        if (!chosen.isCompleted) chosen.complete(file);
      }).toJS,
    );
    // shortcut: a browser without the `cancel` event (Chrome < 113, Safari < 16.4) never completes
    // this future when the dialog is dismissed, so the hidden input also stays in the page, one per
    // dismissal, until the page reloads. Harmless, because nothing waits on it with a spinner;
    // upgrade only if a caller ever needs to know the dialog closed.
    input.addEventListener(
      'cancel',
      ((web.Event _) {
        if (!chosen.isCompleted) chosen.complete(null);
      }).toJS,
    );

    // Attached before the click because Safari has historically ignored a click on a detached file
    // input. Everything above runs synchronously, so the click is still inside the user's gesture.
    web.document.body!.append(input);
    input.click();

    try {
      final file = await chosen.future;
      if (file == null) return null;
      if (file.size > maxBytes) throw FileTooLargeException(file.size);
      return await read(file);
    } finally {
      input.remove();
    }
  }
}
