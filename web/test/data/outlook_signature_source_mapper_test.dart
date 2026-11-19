import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/settings_mapper.dart';

/// `OutlookSignatureSourceDto` as the Settings > Outlook Signatures screen reads it. Placeholders only.
void main() {
  test('reads every field of a source that has pulled', () {
    final source = outlookSignatureSourceFromJson({
      'enabled': true,
      'endpointUrl': 'https://signatures.example.com/bulk',
      'windowsUsernameSuffix': '.ex',
      'isConfigured': true,
      'lastPullUtc': '2026-11-18T05:00:00+00:00',
      'lastPullStatus': 'error',
      'lastPullCount': 12,
      'lastError': 'The source answered 401.',
      'copyCount': 12,
      'lastPullSkipped': 2,
      'lastPullSkippedReason': '2 signatures were skipped: the html or text contains a NUL character (U+0000).',
    });

    expect(source.enabled, isTrue);
    expect(source.endpointUrl, 'https://signatures.example.com/bulk');
    expect(source.windowsUsernameSuffix, '.ex');
    expect(source.isConfigured, isTrue);
    // dateTimeFromJson converts to local time for display; the instant is what must survive.
    expect(source.lastPullUtc!.toUtc(), DateTime.utc(2026, 11, 18, 5));
    expect(source.lastPullStatus, 'error');
    expect(source.lastPullCount, 12);
    expect(source.lastError, 'The source answered 401.');
    expect(source.copyCount, 12);
    expect(source.lastPullSkipped, 2);
    expect(source.lastPullSkippedReason, contains('NUL'));
  });

  test('a source that has never pulled has no status, count or error', () {
    final source = outlookSignatureSourceFromJson({
      'enabled': false,
      'endpointUrl': null,
      'windowsUsernameSuffix': '',
      'isConfigured': false,
      'lastPullUtc': null,
      'lastPullStatus': null,
      'lastPullCount': null,
      'lastError': null,
      'copyCount': 0,
    });

    expect(source.lastPullUtc, isNull);
    expect(source.lastPullStatus, isNull);
    expect(source.lastPullCount, isNull);
    expect(source.lastError, isNull);
    expect(source.endpointUrl, isNull);
    expect(source.copyCount, 0);
    // An older server sends neither field: nothing skipped.
    expect(source.lastPullSkipped, 0);
    expect(source.lastPullSkippedReason, isNull);
  });
}
