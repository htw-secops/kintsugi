import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../../core/di/locator.dart';
import '../../../core/text/invisible_characters.dart';
import '../../../core/theme/kintsugi_palette.dart';
import '../../../core/widgets/alert_box.dart';
import '../../../core/widgets/buttons.dart';
import '../../../core/widgets/form_bits.dart';
import '../../../core/widgets/text_bits.dart';
import '../../../domain/usecases/patch_failure_usecases.dart';
import '../instructions_panel_bloc.dart';
import '../repair_draft_cubit.dart';
import 'instructions_panel.dart';
import 'script_diff_view.dart';

/// "Repair drafted": the repair Kintsugi wrote in the background for a script that keeps failing,
/// as a diff against the script agents run now, with Sign.
///
/// Drafting is automatic and signing is not. The signed script keeps running until somebody presses
/// Sign here, which makes the draft the stored script and signs it through the ordinary checks
/// (`SignUpgradePathScriptCommandHandler`) — so a draft that downloads an installer without checking
/// it is refused here exactly as it would be anywhere.
class RepairDraftPanel extends StatelessWidget {
  const RepairDraftPanel({super.key, required this.failureId, required this.onSigned});

  final String failureId;
  final ValueChanged<SignedScriptOutcome> onSigned;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => RepairDraftCubit(
          failureId: failureId,
          getDraft: locator<GetPatchFailureRepairDraft>(),
          signDraft: locator<SignPatchFailureRepairDraft>(),
        )..load(),
        child: BlocConsumer<RepairDraftCubit, RepairDraftState>(
          listenWhen: (previous, current) => previous.signed == null && current.signed != null,
          listener: (_, state) => onSigned(state.signed!),
          builder: _build,
        ),
      );

  Widget _build(BuildContext context, RepairDraftState state) {
    final palette = context.palette;
    final cubit = context.read<RepairDraftCubit>();
    final draft = state.draft;

    return Container(
      width: double.infinity,
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: palette.accentWash(0.06),
        border: Border.all(color: palette.action),
        borderRadius: BorderRadius.circular(4),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              const SubHeadingTight('Repair drafted'),
              const SizedBox(width: 10),
              if (draft != null) LocalTimestamp(draft.candidateDraftedUtc),
            ],
          ),
          const HintText(
            'Kintsugi wrote this repair from the failure output above. Agents keep running the signed '
            'script until somebody signs this one.',
          ),
          const SizedBox(height: 10),
          if (state.loading)
            const HintText('Loading the drafted repair...')
          else if (state.error case final error?)
            AlertBox.error(error)
          else if (draft != null) ...[
            if (draft.candidateNotes case final notes?) ...[
              const SubHeadingTight('What the AI says it changed'),
              Text(notes, style: Theme.of(context).textTheme.bodyMedium),
              const SizedBox(height: 12),
            ],
            ScriptDiffView(
              diff: state.diff!,
              reviewed: state.reviewed,
              reviewRequired: draft.currentScriptSigned,
              onToggleReviewed: cubit.toggleReviewed,
            ),
            const SizedBox(height: 10),
            Wrap(
              spacing: 12,
              runSpacing: 8,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                PrimaryButton(
                  label: 'Sign Repair',
                  busy: state.signing,
                  onPressed: state.canSign ? () => _sign(context, draft.candidateScript) : null,
                ),
                if (state.unreviewedCount > 0)
                  HintText(
                    '${state.unreviewedCount} change${state.unreviewedCount == 1 ? '' : 's'} left to read '
                    'before signing.',
                  ),
                if (state.signMessage case final message?) HintText(message),
              ],
            ),
          ],
        ],
      ),
    );
  }

  /// Signs, after the same question the instructions panel asks when the script holds characters
  /// the reviewer cannot see.
  Future<void> _sign(BuildContext context, String script) async {
    final cubit = context.read<RepairDraftCubit>();
    final invisible = findInvisible(script);
    if (invisible.isNotEmpty && !await confirmSigningHiddenCharacters(context, invisible)) return;
    await cubit.sign();
  }
}
