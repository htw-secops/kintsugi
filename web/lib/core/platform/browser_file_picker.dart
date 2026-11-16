import 'dart:async';
import 'dart:js_interop';

import 'package:web/web.dart' as web;

import 'file_picker.dart';

/// The real [FilePicker]: a hidden `<input type="file">`, clicked from the press that asked for it
/// (a browser only opens its chooser from a user gesture), whose file is read with `Blob.text()`.
class BrowserFilePicker implements FilePicker {
  const BrowserFilePicker();

  @override
  Future<PickedTextFile?> pickText({String accept = '.csv,text/csv'}) {
    final completer = Completer<PickedTextFile?>();
    final input = web.document.createElement('input') as web.HTMLInputElement
      ..type = 'file'
      ..accept = accept;

    // **Not `async`, and that is not style.** `toJS` only accepts a function that returns `void`: an `async` closure
    // returns a `Future<Null>`, and `flutter build web` refuses it ("Function converted via 'toJS' contains invalid
    // types"). The widget tests run on the Dart VM, which never compiles for the browser, so nothing but a real web
    // build can see this; the file is read with `.then` for that reason.
    input.addEventListener(
      'change',
      ((web.Event _) {
        final file = input.files?.item(0);
        if (file == null) {
          if (!completer.isCompleted) completer.complete(null);
          return;
        }
        unawaited(
          file.text().toDart.then<void>(
            (text) {
              if (!completer.isCompleted) completer.complete(PickedTextFile(name: file.name, text: text.toDart));
            },
            onError: (Object error) {
              if (!completer.isCompleted) completer.completeError(error);
            },
          ),
        );
      }).toJS,
    );
    // Chrome and Firefox raise `cancel` when the chooser is dismissed, which is what lets the dialog
    // go back to waiting for a choice instead of waiting forever on a future nobody completes.
    input.addEventListener(
      'cancel',
      ((web.Event _) {
        if (!completer.isCompleted) completer.complete(null);
      }).toJS,
    );

    input.click();
    return completer.future;
  }
}
