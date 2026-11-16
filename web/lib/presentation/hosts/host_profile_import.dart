import '../../core/text/csv.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';

/// Where each thing the import can use is in a CSV's header row, or null when the file has no such
/// column. Found by **name, case-insensitively**, because a Snipe-IT export's columns are whatever
/// the administrator ticked when exporting.
class HostImportColumns {
  const HostImportColumns({
    this.serial,
    this.department,
    this.location,
    this.model,
    this.assignedName,
    this.assignedEmail,
  });

  final int? serial;
  final int? department;
  final int? location;
  final int? model;
  final int? assignedName;
  final int? assignedEmail;

  /// The names the importer recognises, lower-cased and trimmed. Kept together so the dialog can show
  /// exactly what it looked for when a file has no serial column.
  static const serialNames = ['serial', 'serial number', 'serial no', 'serial no.'];
  static const departmentNames = ['department'];
  static const locationNames = ['location'];
  static const modelNames = ['model', 'model name'];
  static const assignedNameNames = ['checked out to', 'assigned to'];
  static const assignedEmailNames = ['email'];

  static HostImportColumns detect(List<String> header) {
    int? find(List<String> names) {
      final wanted = names.toSet();
      for (var i = 0; i < header.length; i++) {
        if (wanted.contains(header[i].trim().toLowerCase())) return i;
      }
      return null;
    }

    return HostImportColumns(
      serial: find(serialNames),
      department: find(departmentNames),
      location: find(locationNames),
      model: find(modelNames),
      assignedName: find(assignedNameNames),
      assignedEmail: find(assignedEmailNames),
    );
  }

  /// Without a serial number there is nothing to match a host by. A Snipe-IT *users* export has none,
  /// which is the likeliest reason, and why the dialog names the Assets export.
  bool get hasSerial => serial != null;
}

/// A CSV the administrator picked, read and understood but **not yet turned into a request**: which
/// columns it has and how many data rows. Nothing here has left the browser.
class HostImportFile {
  const HostImportFile._({required this.columns, required this.rows, required this.header});

  /// Reads [text]. A file with no rows at all gives an empty header and no columns.
  factory HostImportFile.parse(String text) {
    final all = parseCsv(text);
    if (all.isEmpty) return const HostImportFile._(columns: HostImportColumns(), rows: [], header: []);
    return HostImportFile._(
      columns: HostImportColumns.detect(all.first),
      rows: all.skip(1).toList(),
      header: all.first,
    );
  }

  final HostImportColumns columns;
  final List<String> header;
  final List<List<String>> rows;

  /// Rows that carry a serial number: the ones an import can match.
  int get serialRowCount => columns.serial == null ? 0 : rows.where((r) => _cell(r, columns.serial) != null).length;

  /// The rows to send, holding **only the columns the administrator chose**.
  ///
  /// Department, location and model are the point of the import and are sent when the file has them.
  /// The assigned person's name and email are personal data, so they are in the request only when
  /// [includeName] / [includeEmail] are true. Left out here means they never reach the network: the
  /// server is never sent something it is not asked to keep. An empty department, location or model
  /// cell is left out too, so the server's "an empty field is ignored" rule is never what protects an
  /// existing value. An empty name or email cell in a *chosen* column is sent as `''` instead: in a
  /// Snipe-IT export it means the device is checked out to nobody, and the server clears the person
  /// it had rather than keeping the previous one for good.
  List<HostProfileImportRow> buildRows({bool includeName = false, bool includeEmail = false}) {
    final serial = columns.serial;
    if (serial == null) return const [];

    final result = <HostProfileImportRow>[];
    for (final row in rows) {
      final serialNumber = _cell(row, serial);
      if (serialNumber == null) continue;
      result.add(HostProfileImportRow(
        serialNumber: serialNumber,
        department: _cell(row, columns.department),
        location: _cell(row, columns.location),
        model: _cell(row, columns.model),
        assignedName: includeName ? _person(row, columns.assignedName) : null,
        assignedEmail: includeEmail ? _person(row, columns.assignedEmail) : null,
      ));
    }
    return result;
  }

  /// A cell of a chosen person column: `''` when it is empty (which clears), null only when the file
  /// has no such column at all.
  static String? _person(List<String> row, int? index) => index == null ? null : _cell(row, index) ?? '';

  static String? _cell(List<String> row, int? index) {
    if (index == null || index >= row.length) return null;
    final value = row[index].trim();
    return value.isEmpty ? null : value;
  }
}

/// The server accepts at most this many rows in one request.
const hostImportBatchSize = 10000;

/// Sends [rows] in requests of at most [hostImportBatchSize] and adds the answers up. One request is
/// the normal case; a fleet's whole asset list is a few thousand rows.
Future<HostProfileImportResult> importHostRows(
  ImportHostProfiles importHostProfiles,
  List<HostProfileImportRow> rows, {
  int batchSize = hostImportBatchSize,
}) async {
  var matched = 0;
  var notFound = 0;
  var updated = 0;
  for (var start = 0; start < rows.length; start += batchSize) {
    final end = start + batchSize < rows.length ? start + batchSize : rows.length;
    final answer = await importHostProfiles(rows.sublist(start, end));
    matched += answer.matched;
    notFound += answer.notFound;
    updated += answer.updated;
  }
  return HostProfileImportResult(matched: matched, notFound: notFound, updated: updated);
}
