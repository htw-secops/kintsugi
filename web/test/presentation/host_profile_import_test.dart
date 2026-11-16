import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/domain/entities/host.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/host_usecases.dart';
import 'package:kintsugi_web/presentation/hosts/host_profile_import.dart';

/// A Snipe-IT Assets export, trimmed. Names and emails are invented (@example.com).
const _assets = '﻿Asset Tag,Asset Name,Serial,Model Name,Status,Checked Out To,Location,Department,Email\r\n'
    'HTW-001,ADL-1,S-ONE,Surface Laptop 7,Ready,Alex Morgan,Adelaide,Technology,alex.morgan@example.com\r\n'
    'HTW-002,PTH-2,S-TWO,"HP ProBook 440 G8, 14""",Ready,Sam Taylor,Perth,Residential,sam.taylor@example.com\r\n'
    'HTW-003,NSW-3,,Dell Latitude,Ready,,Sydney,,\r\n'
    'HTW-004,MLB-4,S-FOUR,,Ready,,,HR,\r\n';

class _Repo implements HostRepository {
  final batches = <List<HostProfileImportRow>>[];

  @override
  Future<HostProfileImportResult> importProfiles(List<HostProfileImportRow> rows) async {
    batches.add(rows);
    return HostProfileImportResult(matched: rows.length - 1, notFound: 1, updated: rows.length - 1);
  }

  @override
  noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  group('column detection', () {
    test('finds each column by name, case-insensitively, under the names a Snipe-IT export uses', () {
      final columns = HostImportColumns.detect(
        ['SERIAL NUMBER', 'department', ' Location ', 'Model', 'Assigned To', 'EMAIL'],
      );

      expect(columns.serial, 0);
      expect(columns.department, 1);
      expect(columns.location, 2);
      expect(columns.model, 3);
      expect(columns.assignedName, 4);
      expect(columns.assignedEmail, 5);
    });

    test('accepts the other spellings of each', () {
      expect(HostImportColumns.detect(['Serial']).serial, 0);
      expect(HostImportColumns.detect(['Serial No']).serial, 0);
      expect(HostImportColumns.detect(['Model Name']).model, 0);
      expect(HostImportColumns.detect(['Checked Out To']).assignedName, 0);
    });

    test('a Users export has no serial column, and says so', () {
      final file = HostImportFile.parse('Username,Name,Title,Email,Department,Location,Assets\nu1,A B,Eng,a@example.com,HR,Perth,1\n');

      expect(file.columns.hasSerial, isFalse);
      expect(file.serialRowCount, 0);
      expect(file.buildRows(includeName: true, includeEmail: true), isEmpty);
    });

    test('an empty file has no columns at all', () {
      final file = HostImportFile.parse('');

      expect(file.columns.hasSerial, isFalse);
      expect(file.rows, isEmpty);
    });
  });

  group('the rows that would be sent', () {
    final file = HostImportFile.parse(_assets);

    test('counts the rows that carry a serial number', () {
      expect(file.serialRowCount, 3);
    });

    test('by default hold the serial, department, location and model, and neither the name nor the email', () {
      final rows = file.buildRows();

      expect(rows, hasLength(3));
      expect(rows.first, const HostProfileImportRow(
        serialNumber: 'S-ONE',
        department: 'Technology',
        location: 'Adelaide',
        model: 'Surface Laptop 7',
      ));
      expect(rows.every((r) => r.assignedName == null && r.assignedEmail == null), isTrue);
    });

    test('the unchosen columns never reach the request body: nothing of them is in the JSON', () {
      final body = jsonEncode({'rows': [for (final r in file.buildRows()) r.toJson()]});

      expect(body, isNot(contains('Alex Morgan')));
      expect(body, isNot(contains('Sam Taylor')));
      expect(body, isNot(contains('@example.com')));
      expect(body, isNot(contains('assignedName')));
      expect(body, isNot(contains('assignedEmail')));
      // And the Snipe-IT columns nobody asked for (asset tag, status) are not in it either.
      expect(body, isNot(contains('HTW-001')));
    });

    test('include the name only when it is chosen, the email only when it is chosen', () {
      final nameOnly = file.buildRows(includeName: true);
      expect(nameOnly.first.assignedName, 'Alex Morgan');
      expect(nameOnly.first.assignedEmail, isNull);

      final emailOnly = file.buildRows(includeEmail: true);
      expect(emailOnly.first.assignedEmail, 'alex.morgan@example.com');
      expect(emailOnly.first.assignedName, isNull);
    });

    test('leave an empty department, location or model out, and skip a row with no serial', () {
      final rows = file.buildRows(includeName: true, includeEmail: true);

      // Row 3 has no serial: gone. Row 4 has only a department.
      expect(rows.map((r) => r.serialNumber), ['S-ONE', 'S-TWO', 'S-FOUR']);
      final last = rows.last;
      expect(last.department, 'HR');
      expect(last.location, isNull);
      expect(last.model, isNull);
      expect(last.toJson().keys, containsAll(['serialNumber', 'department']));
      expect(last.toJson().containsKey('location'), isFalse);
    });

    test('send an empty name or email as "" when that column is chosen, so a device checked back in loses its person', () {
      final last = file.buildRows(includeName: true, includeEmail: true).last;
      expect(last.assignedName, '');
      expect(last.assignedEmail, '');
      expect(last.toJson(), containsPair('assignedName', ''));

      // Not chosen: not in the request at all, so what is stored is kept.
      expect(file.buildRows().last.toJson().containsKey('assignedName'), isFalse);
    });

    test('read a quoted model with a comma and a quote in it', () {
      expect(file.buildRows()[1].model, 'HP ProBook 440 G8, 14"');
    });
  });

  group('sending', () {
    HostProfileImportRow row(int i) => HostProfileImportRow(serialNumber: 'S$i');

    test('is one request for a normal file', () async {
      final repo = _Repo();

      final result = await importHostRows(ImportHostProfiles(repo), [for (var i = 0; i < 5; i++) row(i)]);

      expect(repo.batches, hasLength(1));
      expect(result, const HostProfileImportResult(matched: 4, notFound: 1, updated: 4));
    });

    test('is split at the server\'s limit and the answers are added up', () async {
      final repo = _Repo();

      final result = await importHostRows(ImportHostProfiles(repo), [for (var i = 0; i < 5; i++) row(i)], batchSize: 2);

      expect(repo.batches.map((b) => b.length), [2, 2, 1]);
      expect(result, const HostProfileImportResult(matched: 1 + 1 + 0, notFound: 3, updated: 1 + 1 + 0));
    });

    test('the limit is the server\'s 10,000 rows', () {
      expect(hostImportBatchSize, 10000);
    });
  });
}
