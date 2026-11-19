import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/baseline_mapper.dart';
import 'package:kintsugi_web/domain/entities/baseline.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/usecases/baseline_usecases.dart';

void main() {
  test('BaselineItemState matches the C# declaration order', () {
    // It crosses the wire as an ordinal, so the positions are the contract — see
    // .claude/rules/hand-mirrored-dtos.md. A member inserted on either side re-maps every value.
    expect(baselineItemStateFromJson(0), BaselineItemState.compliant);
    expect(baselineItemStateFromJson(1), BaselineItemState.drifted);
    expect(baselineItemStateFromJson(2), BaselineItemState.notApplicable);
    expect(baselineItemStateFromJson(3), BaselineItemState.error);
    expect(baselineItemStateFromJson('NotApplicable'), BaselineItemState.notApplicable);
    expect(baselineItemStateFromJson(null), isNull);
  });

  test('a host that has not reported an item reads as no state, not as compliant', () {
    final detail = baselineHostDetailFromJson({
      'hostId': 'h',
      'hostname': 'pc-1',
      'signedVersion': 2,
      'items': [
        {
          'item': {'key': 'copilot.off', 'kind': 'setting', 'scope': 'machine', 'hive': 'HKLM', 'path': r'SOFTWARE\P', 'valueName': 'V'},
          'state': null,
        },
      ],
      'facts': [],
    });

    expect(detail.items.single.state, isNull);
    expect(detail.items.single.item.location, r'HKLM\SOFTWARE\P\V');
  });

  test('the per-item counts are read from the detail', () {
    final detail = baselineDetailFromJson({
      'id': 'b',
      'name': 'Windows workstation',
      'assignedToAllWindowsHosts': true,
      'assignedHostNames': ['pc-1'],
      'versions': [
        {'id': 'v', 'number': 1, 'document': '{}', 'documentSha256': 'x', 'signed': true, 'createdUtc': '2026-11-12T00:00:00Z'},
      ],
      'items': [
        {
          'item': {'key': 'a', 'kind': 'setting', 'scope': 'machine', 'hive': 'HKLM', 'path': 'P', 'valueName': 'V'},
          'counts': {'compliant': 3, 'drifted': 1, 'notApplicable': 0, 'error': 0, 'notReported': 2},
        },
      ],
      'hosts': [],
    });

    expect(detail.signedVersion?.number, 1);
    expect(detail.items.single.counts.drifted, 1);
    expect(detail.items.single.counts.notReported, 2);
  });

  test('the sign hash is lowercase hex SHA-256 over UTF-8, as ScriptContentHash.Of is', () {
    // Known vectors: a change of encoding or case on either side fails here rather than as a 409.
    expect(
      SignBaselineVersion.documentSha256(''),
      'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    );
    expect(
      SignBaselineVersion.documentSha256('abc'),
      'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
    );
  });

  group('IP-871 per-person shapes', () {
    test('an outlook-signature item reads its name and mode, and is judged per person', () {
      final item = baselineItemFromJson({
        'key': 'outlook.signature',
        'kind': 'outlook-signature',
        'scope': 'user',
        'hive': 'HKCU',
        'path': r'Software\Microsoft\Office\16.0\Outlook',
        'valueName': '',
        'mode': 'enforce',
        'signatureName': 'Example Signature',
      });

      expect(item.signatureName, 'Example Signature');
      expect(item.mode, 'enforce');
      expect(item.isPerPerson, isTrue);
      expect(item.isJudged, isTrue);
      expect(item.isSetting, isFalse);
      expect(item.location, 'Outlook signature "Example Signature"');
      expect(item.expected, 'enforce · per user');
    });

    test('a user-scope setting is still judged per host: per-person is decided by kind', () {
      final item = baselineItemFromJson(
          {'key': 'k', 'kind': 'setting', 'scope': 'user', 'hive': 'HKCU', 'path': 'P', 'valueName': 'V'});

      expect(item.isPerPerson, isFalse);
      expect(item.isJudged, isTrue);
      expect(item.signatureName, isNull);
      expect(item.modeLabel, 'audit');
    });

    test('summary and detail carry the application target and its host count', () {
      final summary = baselineSummaryFromJson({'id': 'b', 'name': 'n', 'assignedApplicationName': 'Example Mail Sync'});
      expect(summary.assignedApplicationName, 'Example Mail Sync');
      expect(baselineSummaryFromJson({'id': 'b', 'assignedApplicationName': null}).assignedApplicationName, isNull);

      final detail = baselineDetailFromJson({
        'id': 'b',
        'name': 'n',
        'versions': [],
        'items': [],
        'hosts': [],
        'assignedApplicationName': 'Example Mail Sync',
        'assignedApplicationHostCount': 4,
      });
      expect(detail.assignedApplicationName, 'Example Mail Sync');
      expect(detail.assignedApplicationHostCount, 4);

      // A server older than the field reads as no count, not as an error.
      expect(baselineDetailFromJson({'id': 'b'}).assignedApplicationHostCount, 0);
    });

    test('a person row reads every BaselinePersonRowDto field, with the state as an ordinal', () {
      final row = baselinePersonRowFromJson({
        'hostId': 'h1',
        'hostname': 'pc-1',
        'windowsUser': 'ana.ex',
        'email': 'ana@example.com',
        'resolvedBy': 'username',
        'reason': null,
        'copyRenderedAtUtc': '2026-11-18T01:00:00+00:00',
        'itemKey': 'outlook.signature',
        'state': 1,
        'observed': 'Example Signature',
        'detail': 'The default reply signature differs.',
        'lastCheckedUtc': '2026-11-18T02:00:00+00:00',
        'lastSeenUtc': '2026-11-18T03:00:00+00:00',
      });

      expect(row.hostId, 'h1');
      expect(row.hostname, 'pc-1');
      expect(row.windowsUser, 'ana.ex');
      expect(row.email, 'ana@example.com');
      expect(row.resolvedBy, PersonResolution.username);
      // dateTimeFromJson converts to local time for display; the instant is what must survive.
      expect(row.copyRenderedAtUtc!.toUtc(), DateTime.utc(2026, 11, 18, 1));
      expect(row.itemKey, 'outlook.signature');
      expect(row.state, BaselineItemState.drifted);
      expect(row.observed, 'Example Signature');
      expect(row.detail, 'The default reply signature differs.');
      expect(row.lastCheckedUtc!.toUtc(), DateTime.utc(2026, 11, 18, 2));
      expect(row.lastSeenUtc!.toUtc(), DateTime.utc(2026, 11, 18, 3));
    });

    test('an unresolved session has no email, no state, and keeps its reason', () {
      final row = baselinePersonRowFromJson({
        'hostId': 'h1',
        'hostname': 'pc-1',
        'windowsUser': 'shared',
        'email': null,
        'resolvedBy': null,
        'reason': 'more than one email matches',
        'itemKey': 'outlook.signature',
        'state': null,
        'lastSeenUtc': '2026-11-18T03:00:00Z',
      });

      expect(row.resolvedBy, PersonResolution.unresolved);
      expect(row.reason, 'more than one email matches');
      expect(row.email, isNull);
      expect(row.state, isNull);
      expect(row.copyRenderedAtUtc, isNull);
      expect(personResolutionFromJson('GCPW'), PersonResolution.gcpw);
      expect(personResolutionFromJson('something-new'), PersonResolution.unresolved);
    });
  });
}
