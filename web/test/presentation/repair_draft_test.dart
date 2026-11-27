import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/core/theme/app_theme.dart';
import 'package:kintsugi_web/data/models/patch_failure_mapper.dart';
import 'package:kintsugi_web/data/models/upgrade_path_mapper.dart';
import 'package:kintsugi_web/domain/entities/patch_failure.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/patch_failure_usecases.dart';
import 'package:kintsugi_web/presentation/applications/failed_updates_screen.dart';
import 'package:kintsugi_web/presentation/applications/instructions_panel_bloc.dart';
import 'package:kintsugi_web/presentation/applications/repair_draft_cubit.dart';
import 'package:kintsugi_web/presentation/applications/widgets/repair_draft_panel.dart';

const _current = r'Get-ItemProperty $key | Select -Expand Properties' '\nexit 0\n';
const _candidate = r'Get-ItemProperty $key -ErrorAction SilentlyContinue' '\nexit 0\n';

Map<String, dynamic> _failureJson({String? draftedUtc, String platform = 'Windows'}) => {
      'id': 'f1',
      'hostId': 'h1',
      'hostname': 'host-1',
      'applicationName': 'DBeaver',
      'platform': platform,
      'details': "The property 'Properties' cannot be found on this object.",
      'firstFailedUtc': '2026-11-20T02:00:00Z',
      'lastFailedUtc': '2026-11-26T02:00:00Z',
      'failureCount': 6,
      'resolution': 'Outstanding',
      'hasScript': true,
      'scriptSigned': true,
      'canFix': true,
      'repairDraftedUtc': ?draftedUtc,
    };

final _draftJson = {
  'applicationName': 'DBeaver',
  'platform': 'Windows',
  'currentScript': _current,
  'currentScriptSigned': true,
  'candidateScript': _candidate,
  'candidateNotes': 'Stopped reading Properties on a value-less uninstall key.',
  'candidateDraftedUtc': '2026-11-27T01:00:00Z',
  'candidateFailureId': 'f1',
};

class _Repository implements PatchFailureRepository {
  _Repository(this.failures);

  final List<PatchFailure> failures;
  final signed = <({String application, String platform, String sha256, String failureId})>[];
  ApiException? signError;

  @override
  Future<List<PatchFailure>> list() async => failures;

  @override
  Future<PatchFailureRepairDraft> repairDraft(String id) async => patchFailureRepairDraftFromJson(_draftJson);

  @override
  Future<UpgradePathResult> signRepairDraft({
    required String applicationName,
    required String platform,
    required String scriptSha256,
    required String patchFailureId,
  }) async {
    if (signError case final error?) throw error;
    signed.add((application: applicationName, platform: platform, sha256: scriptSha256, failureId: patchFailureId));
    return upgradePathResultFromJson({
      'applicationName': applicationName,
      'platform': platform,
      'status': 'Found',
      'method': 'Script',
      'script': _candidate,
      'scriptSigned': true,
      'checkedUtc': '2026-11-27T01:05:00Z',
      'approvalOutcome': 'PullRequestOpened',
      'approvalPullRequestUrl': 'https://example.invalid/pull/7',
      'clearedPatchFailures': 3,
    });
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

void main() {
  test('the mapper reads when a repair was drafted, and an older server sends none', () {
    expect(patchFailureFromJson(_failureJson(draftedUtc: '2026-11-27T01:00:00Z')).hasRepairDraft, isTrue);
    expect(patchFailureFromJson(_failureJson()).hasRepairDraft, isFalse);
  });

  test('a package manager\'s row never offers a draft, whatever the server says', () {
    expect(patchFailureFromJson(_failureJson(draftedUtc: '2026-11-27T01:00:00Z', platform: 'pm:winget')).hasRepairDraft, isFalse);
  });

  test('the draft mapper reads both scripts and the notes', () {
    final draft = patchFailureRepairDraftFromJson(_draftJson);

    expect(draft.currentScript, _current);
    expect(draft.candidateScript, _candidate);
    expect(draft.currentScriptSigned, isTrue);
    expect(draft.candidateNotes, contains('value-less'));
    expect(draft.candidateDraftedUtc.toUtc(), DateTime.utc(2026, 11, 27, 1));
  });

  group('the cubit', () {
    late _Repository repository;
    late RepairDraftCubit cubit;

    setUp(() {
      repository = _Repository(const []);
      cubit = RepairDraftCubit(
        failureId: 'f1',
        getDraft: GetPatchFailureRepairDraft(repository),
        signDraft: SignPatchFailureRepairDraft(repository),
      );
    });

    tearDown(() => cubit.close());

    test('holds Sign until every change from the signed script is read', () async {
      await cubit.load();

      expect(cubit.state.unreviewedCount, 1);
      expect(cubit.state.canSign, isFalse);

      cubit.toggleReviewed(cubit.state.diff!.changes.single.key);
      expect(cubit.state.canSign, isTrue);
    });

    test('signs the hash of the draft it showed, as a repair of this failure', () async {
      await cubit.load();
      cubit.toggleReviewed(cubit.state.diff!.changes.single.key);

      await cubit.sign();

      final call = repository.signed.single;
      expect(call.sha256, InstructionsPanelBloc.scriptSha256(_candidate));
      expect(call.failureId, 'f1');
      expect(call.platform, 'Windows');
      expect(cubit.state.signed?.clearedPatchFailures, 3);
      expect(cubit.state.signed?.approvalPullRequestUrl, 'https://example.invalid/pull/7');
    });

    test('a 409 says the draft changed, and nothing reads as signed', () async {
      repository.signError = const ApiException('conflict', statusCode: 409);
      await cubit.load();
      cubit.toggleReviewed(cubit.state.diff!.changes.single.key);

      await cubit.sign();

      expect(cubit.state.signed, isNull);
      expect(cubit.state.signMessage, contains('no longer the one shown here'));
    });
  });

  group('the screen', () {
    late _Repository repository;

    setUp(() {
      repository = _Repository([patchFailureFromJson(_failureJson(draftedUtc: '2026-11-27T01:00:00Z'))]);
      locator
        ..registerSingleton(GetPatchFailures(repository))
        ..registerSingleton(DismissPatchFailure(repository))
        ..registerSingleton(ReverifyPatchFailures(repository))
        ..registerSingleton(GetPatchFailureRepairDraft(repository))
        ..registerSingleton(SignPatchFailureRepairDraft(repository));
    });

    tearDown(() => locator.reset());

    void wide(WidgetTester tester) {
      tester.view.physicalSize = const Size(1500, 1400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
    }

    testWidgets('a row with a draft says so before it is opened', (tester) async {
      wide(tester);
      await tester.pumpWidget(MaterialApp(theme: AppTheme.light(), home: const Scaffold(body: FailedUpdatesScreen())));
      await tester.pumpAndSettle();

      expect(find.text('REPAIR DRAFTED'), findsOneWidget);
    });

    testWidgets('the panel shows the notes and the diff, and Sign hands the outcome to the screen', (tester) async {
      wide(tester);
      SignedScriptOutcome? outcome;
      await tester.pumpWidget(MaterialApp(
        theme: AppTheme.light(),
        home: Scaffold(
          body: SingleChildScrollView(child: RepairDraftPanel(failureId: 'f1', onSigned: (o) => outcome = o)),
        ),
      ));
      await tester.pumpAndSettle();

      expect(find.textContaining('value-less uninstall key'), findsOneWidget);
      expect(find.text('1 change left to read before signing.'), findsOneWidget);

      await tester.tap(find.byKey(const ValueKey('review-0')));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Sign Repair'));
      await tester.pumpAndSettle();

      expect(repository.signed, hasLength(1));
      expect(outcome?.clearedPatchFailures, 3);
    });
  });
}
