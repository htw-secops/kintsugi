/// A text file the administrator chose, already read.
class PickedTextFile {
  const PickedTextFile({required this.name, required this.text});

  final String name;
  final String text;
}

/// Lets the administrator choose a file from their own computer and hands its text back.
///
/// Behind an interface for the reason `FileDownloader` is: the screens that import stay testable off
/// the web target, and the browser implementation is the only file that builds an `<input type=file>`.
/// The file is read **in the browser**; nothing is uploaded by choosing it. What the screen then sends
/// to the server is whatever the screen decides to send.
abstract interface class FilePicker {
  /// Opens the browser's file chooser. Null when it is cancelled. [accept] is the browser's own filter
  /// string, a hint rather than a guarantee: the person can still pick another type.
  Future<PickedTextFile?> pickText({String accept = '.csv,text/csv'});
}
