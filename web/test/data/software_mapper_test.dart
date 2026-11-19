import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/software_mapper.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/software_title.dart';

/// The Software screen's DTOs, read the way `AdminSoftwareController` writes them: `SoftwareMode` and
/// `SoftwareScriptState` as **names**.
void main() {
  test('reads a title and a secret summary, with the enums as names', () {
    final overview = softwareOverviewFromJson({
      'titles': [
        {
          'id': 't1',
          'name': 'Greenshot',
          'platform': 'pm:winget',
          'packageManager': 'winget',
          'packageId': 'Greenshot.Greenshot',
          'detectAs': null,
          'mode': 'SelfService',
          'secretNames': ['EXAMPLE_KEY'],
          'assignedToAll': true,
          'assignedHostNames': ['host-a'],
          'compliantCount': 3,
          'pendingCount': 2,
          'failedCount': 1,
          'notApplicableCount': 4,
          'scriptState': 'NoActionMode',
        },
      ],
      'secrets': [
        {'name': 'EXAMPLE_KEY', 'hasValue': true, 'updatedBy': 'admin', 'updatedUtc': '2026-11-19T01:02:03+00:00'},
      ],
    });

    final title = overview.titles.single;
    expect(title.mode, SoftwareMode.selfService);
    expect(title.scriptState, SoftwareScriptState.noActionMode);
    expect(title.operatingSystem, 'Windows');
    expect(title.managerBucket, 'pm:winget');
    expect(title.isCustom, isFalse);
    expect(
      [title.compliantCount, title.pendingCount, title.failedCount, title.notApplicableCount],
      [3, 2, 1, 4],
    );

    final secret = overview.secrets.single;
    expect(secret.name, 'EXAMPLE_KEY');
    expect(secret.hasValue, isTrue);
    expect(secret.updatedUtc, isNotNull);
  });

  test('a mode or script state this client does not know is null, never a guess', () {
    final title = softwareTitleFromJson({'id': 'x', 'name': 'x', 'platform': 'Linux', 'mode': 'Quarantined', 'scriptState': 2});

    expect(title.mode, isNull);
    expect(title.scriptState, isNull);
    expect(title.isCustom, isTrue);
    expect(title.operatingSystem, 'Linux');
  });

  test('every mode and script state round-trips through its wire name', () {
    for (final mode in SoftwareMode.values) {
      expect(softwareModeFromJson(mode.wireName), mode);
    }
    for (final state in SoftwareScriptState.values) {
      expect(softwareScriptStateFromJson(state.wireName), state);
    }
  });

  test('a draft sends the mode as a name, and a platform only for a custom title', () {
    const managed = SoftwareTitleDraft(
      name: 'Greenshot',
      packageManager: 'winget',
      platform: 'Windows',
      packageId: 'Greenshot.Greenshot',
      mode: SoftwareMode.prohibited,
    );
    const custom = SoftwareTitleDraft(name: 'Example App', platform: 'macOS', mode: SoftwareMode.required);

    expect(managed.toJson()['mode'], 'Prohibited');
    expect(managed.toJson()['platform'], isNull);
    expect(custom.toJson()['platform'], 'macOS');
    expect(custom.toJson()['packageManager'], isNull);
  });

  test('a run-now answer carries the counts, and the screen only when the server sent it', () {
    final bare = softwareRunNowResultFromJson({'requested': 2, 'notRequested': ['host-b']});
    final full = softwareRunNowResultFromJson({'requested': 0, 'notRequested': [], 'titles': [], 'secrets': []});

    expect(bare.result.requested, 2);
    expect(bare.result.notRequested, ['host-b']);
    expect(bare.overview, isNull);
    expect(full.overview, isNotNull);
  });

  test('a secret summary never picks up a value, even if one were sent', () {
    final secret = scriptSecretSummaryFromJson({'name': 'EXAMPLE_KEY', 'hasValue': true, 'value': 'should-not-appear'});

    expect(secret.toString(), isNot(contains('should-not-appear')));
    expect(secret.props, isNot(contains('should-not-appear')));
  });
}
