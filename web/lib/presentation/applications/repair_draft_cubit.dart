import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../core/text/script_diff.dart';
import '../../domain/entities/patch_failure.dart';
import '../../domain/usecases/patch_failure_usecases.dart';
import 'instructions_panel_bloc.dart';

class RepairDraftState extends Equatable {
  const RepairDraftState({
    this.loading = true,
    this.draft,
    this.diff,
    this.error,
    this.reviewed = const {},
    this.signing = false,
    this.signMessage,
    this.signed,
  });

  final bool loading;
  final PatchFailureRepairDraft? draft;

  /// The running script against the draft, worked out once on load rather than on every build.
  final ScriptDiff? diff;
  final String? error;

  /// The [ScriptChange.key]s marked as read.
  final Set<String> reviewed;
  final bool signing;
  final String? signMessage;

  /// Set once the draft is signed; the screen then closes the row and says what happened.
  final SignedScriptOutcome? signed;

  /// Changes not yet marked as read. Only counted when the running script is signed: as in the
  /// instructions panel, the difference from a trusted script is the thing a signature vouches for.
  int get unreviewedCount => draft?.currentScriptSigned != true || diff == null
      ? 0
      : diff!.changes.where((change) => !reviewed.contains(change.key)).length;

  bool get canSign => draft != null && !signing && signed == null && unreviewedCount == 0;

  RepairDraftState copyWith({Set<String>? reviewed, bool? signing, String? signMessage, SignedScriptOutcome? signed}) =>
      RepairDraftState(
        loading: loading,
        draft: draft,
        diff: diff,
        error: error,
        reviewed: reviewed ?? this.reviewed,
        signing: signing ?? this.signing,
        signMessage: signMessage ?? this.signMessage,
        signed: signed ?? this.signed,
      );

  @override
  List<Object?> get props => [loading, draft, diff, error, reviewed, signing, signMessage, signed];
}

/// The "Repair drafted" panel on a Failed Updates row: loads the draft Kintsugi wrote in the
/// background, holds which of its changes have been read, and signs it.
///
/// Signing sends the SHA-256 of the draft exactly as loaded, so the server signs only the text this
/// panel showed — a draft the background service replaced in the meantime is a 409, not a signature.
class RepairDraftCubit extends Cubit<RepairDraftState> {
  RepairDraftCubit({
    required this.failureId,
    required GetPatchFailureRepairDraft getDraft,
    required SignPatchFailureRepairDraft signDraft,
  })  : _getDraft = getDraft,
        _signDraft = signDraft,
        super(const RepairDraftState());

  final String failureId;
  final GetPatchFailureRepairDraft _getDraft;
  final SignPatchFailureRepairDraft _signDraft;

  Future<void> load() async {
    try {
      final draft = await _getDraft(failureId);
      emit(RepairDraftState(loading: false, draft: draft, diff: diffScripts(draft.currentScript ?? '', draft.candidateScript)));
    } on ApiException catch (error) {
      emit(RepairDraftState(
        loading: false,
        error: error.statusCode == 404
            ? 'The drafted repair is no longer waiting: it was signed, or the script it repaired was replaced. Reload the page.'
            : 'Could not load the drafted repair: ${error.message}',
      ));
    }
  }

  void toggleReviewed(String key) => emit(state.copyWith(
        reviewed: state.reviewed.contains(key) ? ({...state.reviewed}..remove(key)) : {...state.reviewed, key},
      ));

  Future<void> sign() async {
    final draft = state.draft;
    if (draft == null || !state.canSign) return;

    emit(state.copyWith(signing: true, signMessage: 'Signing...'));
    try {
      final signed = await _signDraft(
        applicationName: draft.applicationName,
        platform: draft.platform,
        scriptSha256: InstructionsPanelBloc.scriptSha256(draft.candidateScript),
        patchFailureId: failureId,
      );
      emit(state.copyWith(
        signing: false,
        signMessage: 'Signed.',
        signed: SignedScriptOutcome(
          clearedPatchFailures: signed.clearedPatchFailures,
          approvalDescription: InstructionsPanelBloc.describeApproval(signed),
          approvalPullRequestUrl: signed.approvalPullRequestUrl,
        ),
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(
        signing: false,
        signMessage: error.statusCode == 409
            ? 'Not signed: the draft on the server is no longer the one shown here. It was replaced or '
                'signed after this panel loaded it. Reload the page and review the current one.'
            : 'Not signed: ${error.message}',
      ));
    }
  }
}
