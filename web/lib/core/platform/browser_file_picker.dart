import 'dart:async';
import 'dart:js_interop';
import 'dart:typed_data';

import 'package:web/web.dart' as web;

import 'file_picker.dart';

/// The real [FilePicker]: a hidden `<input type="file">`, clicked once and thrown away. One of the
/// few files in this app that touch the DOM. Nothing is uploaded here — the bytes are read in the
/// browser and travel with the next save like any other field.
class BrowserFilePicker implements FilePicker {
  const BrowserFilePicker();

  @override
  Future<Uint8List?> pick({required String accept}) async {
    final input = web.HTMLInputElement()
      ..type = 'file'
      ..accept = accept
      ..style.display = 'none';
    final chosen = Completer<web.File?>();

    input.addEventListener(
      'change',
      ((web.Event _) {
        final files = input.files;
        final file = files != null && files.length > 0 ? files.item(0) : null;
        if (!chosen.isCompleted) chosen.complete(file);
      }).toJS,
    );
    // shortcut: a browser without the `cancel` event (Chrome < 113, Safari < 16.4) never completes
    // this future when the dialog is dismissed. Harmless, because nothing waits on it with a spinner;
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
      final buffer = await file.arrayBuffer().toDart;
      return buffer.toDart.asUint8List();
    } finally {
      input.remove();
    }
  }
}
