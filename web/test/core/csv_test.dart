import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/text/csv.dart';

/// The CSV reader behind the host import. A real export has every awkward thing below, and one of them
/// handled wrongly costs a serial number a leading zero or a header its first letter.
void main() {
  test('reads rows and cells', () {
    expect(parseCsv('a,b\n1,2\n'), [
      ['a', 'b'],
      ['1', '2'],
    ]);
  });

  test('drops a byte-order mark, so the first header is not "\\uFEFFSerial"', () {
    final rows = parseCsv('﻿Serial,Department\nS1,HR\n');

    expect(rows.first.first, 'Serial');
    expect(rows.first.first.codeUnitAt(0), isNot(0xFEFF));
  });

  test('reads CRLF, LF and bare CR line endings, and a last line with no ending', () {
    const expected = [
      ['a', 'b'],
      ['1', '2'],
      ['3', '4'],
    ];

    expect(parseCsv('a,b\r\n1,2\r\n3,4'), expected);
    expect(parseCsv('a,b\n1,2\n3,4\n'), expected);
    expect(parseCsv('a,b\r1,2\r3,4\r'), expected);
  });

  test('reads quoted cells: commas, doubled quotes and line breaks inside quotes', () {
    final rows = parseCsv('a,"b, c","say ""hi""","line1\nline2"\r\nz,y,x,w\r\n');

    expect(rows, [
      ['a', 'b, c', 'say "hi"', 'line1\nline2'],
      ['z', 'y', 'x', 'w'],
    ]);
  });

  test('a CRLF inside quotes stays in the cell', () {
    expect(parseCsv('"a\r\nb",c\n'), [
      ['a\r\nb', 'c'],
    ]);
  });

  test('keeps empty cells, and drops blank lines', () {
    expect(parseCsv('a,,c\n\n,,\n\n'), [
      ['a', '', 'c'],
      ['', '', ''],
    ]);
  });

  test('changes nothing it reads: leading zeros and spaces survive', () {
    expect(parseCsv('007, a ,B\n'), [
      ['007', ' a ', 'B'],
    ]);
  });

  test('an empty file is no rows', () {
    expect(parseCsv(''), isEmpty);
    expect(parseCsv('﻿'), isEmpty);
    expect(parseCsv('\n\r\n'), isEmpty);
  });
}
