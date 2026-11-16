/// Reads comma-separated text into rows of cells, following RFC 4180 closely enough for the exports
/// the admin UI imports (Snipe-IT's, in practice).
///
/// What it handles, because a real export has all of it:
///  * a **byte-order mark** at the start (Excel and several exporters write one, and left in it makes
///    the first header read as `﻿Serial`, which matches nothing);
///  * **CRLF, LF and bare CR** line endings, and a final line with no ending at all;
///  * **quoted cells**, with `""` for a literal quote, and with commas and *line breaks inside the
///    quotes* (an asset's notes are free text);
///  * **blank lines**, which are dropped rather than becoming a row with one empty cell.
///
/// It does not guess a delimiter, trim cells or interpret anything: that is the caller's business,
/// and a parser that quietly "fixes" its input is how a serial number loses a leading zero.
List<List<String>> parseCsv(String text) {
  var input = text;
  if (input.startsWith('﻿')) input = input.substring(1);

  final rows = <List<String>>[];
  var row = <String>[];
  final cell = StringBuffer();
  var inQuotes = false;
  // Whether this record has had any content, so a line holding only a line break is dropped but a
  // line holding `,` (two empty cells) is not.
  var started = false;

  void endCell() {
    row.add(cell.toString());
    cell.clear();
  }

  void endRow() {
    if (started || row.isNotEmpty) {
      endCell();
      final blank = row.length == 1 && row.first.isEmpty;
      if (!blank) rows.add(row);
    }
    row = <String>[];
    started = false;
  }

  for (var i = 0; i < input.length; i++) {
    final c = input[i];

    if (inQuotes) {
      if (c == '"') {
        if (i + 1 < input.length && input[i + 1] == '"') {
          cell.write('"');
          i++;
        } else {
          inQuotes = false;
        }
      } else {
        cell.write(c);
      }
      continue;
    }

    switch (c) {
      case '"':
        inQuotes = true;
        started = true;
      case ',':
        started = true;
        endCell();
      case '\r':
        endRow();
        if (i + 1 < input.length && input[i + 1] == '\n') i++;
      case '\n':
        endRow();
      default:
        started = true;
        cell.write(c);
    }
  }
  endRow();
  return rows;
}
