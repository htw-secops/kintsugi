import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/text/script_diff.dart';
import 'package:kintsugi_web/domain/entities/upgrade_path.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/upgrade_path_usecases.dart';
import 'package:kintsugi_web/presentation/applications/instructions_panel_bloc.dart';

import 'instructions_panel_bloc_test.dart' show result;

/// A repository that holds one script, saves what it is given, signs what it holds, and answers a
/// "Send to AI" with [aiScript].
class _Repo implements UpgradePathRepository {
  _Repo({required this.stored});

  UpgradePathResult? stored;
  final String aiScript = '#!/bin/bash\necho ai';

  /// Whether a "Send to AI" has been made: until then there is no run to report on, and the panel's
  /// own poll on opening must find nothing.
  bool _asked = false;

  @override
  Future<UpgradePathPrompt> prompt({required String applicationName, String? platform, String? patchFailureId}) async =>
      UpgradePathPrompt(
        available: true,
        platform: 'macOS',
        prompt: 'Research it.',
        reason: null,
        existingResult: stored,
      );

  @override
  Future<UpgradePathResult> save(Map<String, dynamic> body) async =>
      stored = result(script: body['script'] as String?);

  @override
  Future<UpgradePathResult> signScript({
    required String applicationName,
    required String platform,
    String? patchFailureId,
  }) async =>
      stored = result(script: stored?.script, scriptSigned: true);

  @override
  Future<RunStarted<UpgradePathRefreshStatus>> startRefresh({
    required String applicationName,
    String? platform,
    String? instructions,
  }) async {
    _asked = true;
    return const RunStarted(started: true, status: UpgradePathRefreshStatus.idle());
  }

  @override
  Future<UpgradePathRefreshStatus> refreshStatus(String applicationName) async => !_asked
      ? const UpgradePathRefreshStatus.idle()
      : UpgradePathRefreshStatus(
          isRunning: false,
          startedUtc: DateTime.utc(2026, 11, 4),
          completedUtc: DateTime.utc(2026, 11, 4),
          success: true,
          errorMessage: null,
          results: [stored = result(script: aiScript)],
        );

  @override
  dynamic noSuchMethod(Invocation invocation) => throw UnimplementedError();
}

const _signed = '#!/bin/bash\nset -e\ncurl -fsSL https://a.example/x -o f\necho done';

InstructionsPanelBloc _bloc(_Repo repo) => InstructionsPanelBloc(
      applicationName: 'Nextcloud',
      platform: 'macOS',
      getPrompt: GetUpgradePathPrompt(repo),
      startRefresh: StartUpgradePathRefresh(repo),
      refreshStatus: GetUpgradePathRefreshStatus(repo),
      saveUpgradePath: SaveUpgradePath(repo),
      signScript: SignUpgradePathScript(repo),
    );

Future<void> _settle() => Future<void>.delayed(const Duration(milliseconds: 20));

/// What the panel remembers so that a changed script is read against the one that was trusted, and
/// what Clear and Undo clear leave behind.
void main() {
  Future<InstructionsPanelBloc> open(_Repo repo) async {
    final bloc = _bloc(repo)..add(const PanelOpened());
    await _settle();
    return bloc;
  }

  group('the script to compare against', () {
    test('is the one the row opened with, signed or not', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));

      expect(bloc.state.baselineText, _signed);
      expect(bloc.state.baselineSigned, isTrue);
      expect(bloc.state.changeKeys, isEmpty, reason: 'nothing differs yet');
      await bloc.close();
    });

    test('is nothing for a row that had no script', () async {
      final bloc = await open(_Repo(stored: null));

      expect(bloc.state.baselineText, isNull);
      expect(bloc.state.changeKeys, isEmpty);
      await bloc.close();
    });

    test('survives a save and a Send to AI, which both overwrite the stored script', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));

      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho edited'));
      await _settle();
      expect(bloc.state.baselineText, _signed);

      bloc.add(const InstructionsSent('again'));
      await _settle();
      bloc.add(const RefreshStatusPolled());
      await _settle();
      expect(bloc.state.baselineText, _signed);
      await bloc.close();
    });

    test('becomes the script that was just signed', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));
      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho edited'));
      await _settle();
      for (final key in bloc.state.changeKeys) {
        bloc.add(ChangeReviewToggled(key));
      }
      await _settle();

      bloc.add(const ScriptSignRequested());
      await _settle();

      expect(bloc.state.baselineText, '#!/bin/bash\necho edited');
      expect(bloc.state.baselineSigned, isTrue);
      expect(bloc.state.changeKeys, isEmpty);
      expect(bloc.state.reviewed, isEmpty, reason: 'what was read is now what is trusted');
      await bloc.close();
    });
  });

  group('signing a script that differs from the signed one', () {
    test('waits until every change is marked as read', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));

      bloc.add(const ScriptSaveRequested('#!/bin/bash\nset -e\ncurl -fsSL https://b.example/x -o f\necho done'));
      await _settle();

      expect(bloc.state.inSyncWithServer, isTrue);
      expect(bloc.state.result!.isSignable, isTrue);
      expect(bloc.state.unreviewedCount, 1);
      expect(bloc.state.canSign, isFalse, reason: 'one change has not been read');

      bloc.add(ChangeReviewToggled(bloc.state.changeKeys.single));
      await _settle();

      expect(bloc.state.unreviewedCount, 0);
      expect(bloc.state.canSign, isTrue);
      await bloc.close();
    });

    test('counts each separate change', () async {
      final bloc = await open(_Repo(stored: result(script: '1\n2\n3\n4\n5\n6', scriptSigned: true)));

      bloc.add(const ScriptSaveRequested('one\n2\n3\n4\n5\nsix'));
      await _settle();

      expect(bloc.state.changeKeys, hasLength(2));
      bloc.add(ChangeReviewToggled(bloc.state.changeKeys.first));
      await _settle();
      expect(bloc.state.unreviewedCount, 1);
      expect(bloc.state.canSign, isFalse);
      await bloc.close();
    });

    test('lets a mark be taken back', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));
      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho edited'));
      await _settle();
      final key = bloc.state.changeKeys.single;

      bloc.add(ChangeReviewToggled(key));
      await _settle();
      expect(bloc.state.canSign, isTrue);

      bloc.add(ChangeReviewToggled(key));
      await _settle();
      expect(bloc.state.canSign, isFalse);
      await bloc.close();
    });

    test('does not carry a mark over to a change that has since been edited', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));
      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho first'));
      await _settle();
      bloc.add(ChangeReviewToggled(bloc.state.changeKeys.single));
      await _settle();
      expect(bloc.state.canSign, isTrue);

      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho second'));
      await _settle();

      expect(bloc.state.unreviewedCount, 1, reason: 'a different change is a different thing to read');
      expect(bloc.state.canSign, isFalse);
      await bloc.close();
    });

    test('is not held when the new script is the same as the signed one', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));

      bloc.add(const ScriptSaveRequested(_signed));
      await _settle();

      expect(bloc.state.changeKeys, isEmpty);
      expect(bloc.state.canSign, isTrue);
      await bloc.close();
    });

    test('is held for a script the AI wrote, until its changes are read', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));

      bloc.add(const InstructionsSent('rewrite it'));
      await _settle();
      bloc.add(const RefreshStatusPolled());
      await _settle();

      expect(bloc.state.result!.script, '#!/bin/bash\necho ai');
      expect(bloc.state.changeKeys, isNotEmpty);
      expect(bloc.state.canSign, isFalse);
      await bloc.close();
    });

    test('is never held when the row opened with a script nobody had signed', () async {
      final bloc = await open(_Repo(stored: result(script: _signed)));

      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho edited'));
      await _settle();

      expect(bloc.state.changeKeys, isEmpty, reason: 'there is no trusted version to differ from');
      expect(bloc.state.canSign, isTrue);
      await bloc.close();
    });

    test('is never held for a row that had no script to begin with', () async {
      final bloc = await open(_Repo(stored: null));

      bloc.add(const ScriptSaveRequested('#!/bin/bash\necho new'));
      await _settle();

      expect(bloc.state.canSign, isTrue);
      await bloc.close();
    });

    test('agrees with the comparison the Changes tab shows', () async {
      final bloc = await open(_Repo(stored: result(script: _signed, scriptSigned: true)));
      const edited = '#!/bin/bash\nset -e\ncurl -fsSL https://b.example/x -o f\necho done\nrm -rf /tmp/x';

      bloc.add(const ScriptSaveRequested(edited));
      await _settle();

      expect(bloc.state.changeKeys, [for (final c in diffScripts(_signed, edited).changes) c.key]);
      await bloc.close();
    });
  });

  group('Clear', () {
    test('empties the box and remembers what it held', () async {
      final bloc = await open(_Repo(stored: result(script: _signed)));

      bloc.add(const EditorCleared(_signed));
      await _settle();

      expect(bloc.state.editorText, '');
      expect(bloc.state.clearedText, _signed);
      expect(bloc.state.inSyncWithServer, isFalse, reason: 'the box no longer matches what the server holds');
      expect(bloc.state.canSign, isFalse);
      await bloc.close();
    });

    test('sends nothing: the stored script is untouched', () async {
      final repo = _Repo(stored: result(script: _signed));
      final bloc = await open(repo);

      bloc.add(const EditorCleared(_signed));
      await _settle();

      expect(repo.stored!.script, _signed);
      expect(bloc.state.result!.script, _signed);
      await bloc.close();
    });

    test('is undone by Undo clear, which puts back the text and whether it matched the server', () async {
      final bloc = await open(_Repo(stored: result(script: _signed)));
      bloc.add(const EditorCleared(_signed));
      await _settle();

      bloc.add(const EditorClearUndone());
      await _settle();

      expect(bloc.state.editorText, _signed);
      expect(bloc.state.inSyncWithServer, isTrue);
      expect(bloc.state.clearedText, isNull, reason: 'it can only be undone once');
      await bloc.close();
    });

    test('cannot be undone once something else has been typed', () async {
      final bloc = await open(_Repo(stored: result(script: _signed)));
      bloc.add(const EditorCleared(_signed));
      await _settle();

      bloc.add(const EditorTouched());
      await _settle();

      expect(bloc.state.clearedText, isNull);
      await bloc.close();
    });

    test('Undo clear with nothing cleared does nothing', () async {
      final bloc = await open(_Repo(stored: result(script: _signed)));
      final before = bloc.state;

      bloc.add(const EditorClearUndone());
      await _settle();

      expect(bloc.state, before);
      await bloc.close();
    });

    test('keeps what was not in sync as not in sync when undone', () async {
      final bloc = await open(_Repo(stored: result(script: _signed)));
      bloc.add(const EditorTouched());
      await _settle();
      bloc.add(const EditorCleared('typed but unsaved'));
      await _settle();

      bloc.add(const EditorClearUndone());
      await _settle();

      expect(bloc.state.editorText, 'typed but unsaved');
      expect(bloc.state.inSyncWithServer, isFalse);
      await bloc.close();
    });
  });
}
