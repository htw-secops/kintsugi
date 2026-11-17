import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/platform/file_picker.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'patching_policy_bloc.dart';
import 'settings_state.dart';

/// How often agents patch, how far a required restart can be deferred, and what the Windows agent's
/// end-user popups are branded with. What `Pages/Settings/PatchingPolicy.cshtml` was.
class PatchingPolicySettingsScreen extends StatelessWidget {
  const PatchingPolicySettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => PatchingPolicyBloc(
          getSettings: locator<GetPatchingPolicySettings>(),
          updateSettings: locator<UpdatePatchingPolicySettings>(),
        )..add(const PatchingPolicyRequested()),
        child: const _PatchingPolicyForm(),
      );
}

class _PatchingPolicyForm extends StatefulWidget {
  const _PatchingPolicyForm();

  @override
  State<_PatchingPolicyForm> createState() => _PatchingPolicyFormState();
}

class _PatchingPolicyFormState extends State<_PatchingPolicyForm> {
  final _intervalValue = TextEditingController();
  final _delayValue = TextEditingController();
  final _maxDelayCount = TextEditingController();
  final _organisationName = TextEditingController();

  PatchingTimeUnit _intervalUnit = PatchingTimeUnit.days;
  PatchingTimeUnit _delayUnit = PatchingTimeUnit.days;

  // Held here rather than in the bloc, like the units above: it is form state until Save is pressed.
  Uint8List? _logo;

  /// Why the last file picked was refused. Cleared by a good pick, a removal or a reload.
  String? _logoError;

  @override
  void dispose() {
    _intervalValue.dispose();
    _delayValue.dispose();
    _maxDelayCount.dispose();
    _organisationName.dispose();
    super.dispose();
  }

  /// Fills the controls from whatever the server holds.
  ///
  /// Driven by a listener rather than by the builder, because a builder that assigned to a
  /// controller would fight the operator for the cursor on every rebuild — and rebuilds happen on
  /// every keystroke.
  void _hydrate(PatchingPolicySettings settings) {
    _intervalValue.text = settings.intervalValue.toString();
    _delayValue.text = settings.delayValue.toString();
    _maxDelayCount.text = settings.maxDelayCount.toString();
    _organisationName.text = settings.organisationName ?? '';
    setState(() {
      _intervalUnit = settings.intervalUnit;
      _delayUnit = settings.delayUnit;
      _logo = settings.organisationLogoPng;
      _logoError = null;
    });
  }

  /// Opens the browser's file dialog. Nothing may be awaited before the call: the browser opens the
  /// dialog only inside the click's user activation (see [FilePicker.pick]).
  Future<void> _pickLogo() async {
    Uint8List? bytes;
    try {
      bytes = await locator<FilePicker>().pick(accept: '.png,image/png');
    } catch (_) {
      // A file removed or unreadable after it was chosen. Said under the field rather than left as
      // an uncaught error from a button press, which would show nothing at all.
      if (mounted) setState(() => _logoError = 'That file could not be read. Try choosing it again.');
      return;
    }
    if (bytes == null || !mounted) return;

    final problem = PatchingPolicySettings.logoProblem(bytes);
    setState(() {
      // A refused file leaves the current logo where it was, so a mistaken pick loses nothing.
      if (problem == null) _logo = bytes;
      _logoError = problem;
    });
    context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited());
  }

  void _removeLogo() {
    setState(() {
      _logo = null;
      _logoError = null;
    });
    context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited());
  }

  void _save() {
    // A refused pick's message would otherwise outlive the save: the logo it refers to was never
    // kept, so an unchanged policy comes back equal and nothing else would clear it.
    setState(() => _logoError = null);
    context.read<PatchingPolicyBloc>().add(PatchingPolicySaveRequested(PatchingPolicySettings(
          // Parsed leniently on purpose: the server validates these, and rejecting a half-typed
          // number in the client would mean two sets of rules to keep in step. A blank field falls
          // back to the default the server itself would use.
          intervalValue: int.tryParse(_intervalValue.text) ?? 7,
          intervalUnit: _intervalUnit,
          delayValue: int.tryParse(_delayValue.text) ?? 1,
          delayUnit: _delayUnit,
          maxDelayCount: int.tryParse(_maxDelayCount.text) ?? 3,
          // Sent as typed: the server trims it and treats blank as none.
          organisationName: _organisationName.text,
          organisationLogoPng: _logo,
        )));
  }

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<PatchingPolicyBloc, SettingsState<PatchingPolicySettings>>(
        listenWhen: (previous, current) => previous.value != current.value && current.value != null,
        listener: (context, state) => _hydrate(state.value!),
        builder: (context, state) => PageScaffold(
          title: 'Patching Policy',
          subtitle: 'How often the kintsugi-agent should check for and apply patches, and — when '
              'installing one needs an application restart or a host reboot — how long that can be '
              'deferred and how many times before it must go through regardless.',
          children: [
            if (state.error != null) AlertBox.error(state.error!),
            if (state.saved) const AlertBox.success('Patching policy saved.'),
            SettingsFormPanel(
              children: [
                LabelledField(
                  label: 'Patch every',
                  hints: const [HintText('How often the agent should check for and apply patches.')],
                  child: _ValueAndUnit(
                    controller: _intervalValue,
                    unit: _intervalUnit,
                    onUnitChanged: (unit) => setState(() => _intervalUnit = unit),
                    onEdited: () => context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                    errorText: state.errorFor('IntervalValue'),
                  ),
                ),
                LabelledField(
                  label: 'Delay length',
                  hints: const [
                    HintText(
                      'How long a single deferral lasts, when a required restart or reboot is '
                      'postponed.',
                    ),
                  ],
                  child: _ValueAndUnit(
                    controller: _delayValue,
                    unit: _delayUnit,
                    onUnitChanged: (unit) => setState(() => _delayUnit = unit),
                    onEdited: () => context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                    errorText: state.errorFor('DelayValue'),
                  ),
                ),
                LabelledField(
                  label: 'Maximum number of delays',
                  hints: const [
                    HintText(
                      'How many times a required restart or reboot can be postponed before the agent '
                      'must force it through. Zero means it can never be deferred.',
                    ),
                  ],
                  child: SizedBox(
                    width: 128,
                    child: KintsugiTextField(
                      controller: _maxDelayCount,
                      keyboardType: TextInputType.number,
                      errorText: state.errorFor('MaxDelayCount'),
                      onChanged: (_) =>
                          context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                    ),
                  ),
                ),
                const SubHeadingTight('What users see'),
                const HintText('Windows PCs pick up changes at their next check-in.'),
                LabelledField(
                  label: 'Organisation name',
                  child: KintsugiTextField(
                    controller: _organisationName,
                    // A placeholder, not a real organisation: this repository is public.
                    hintText: 'Shown on every popup, e.g. Example Corp IT',
                    errorText: state.errorFor('OrganisationName'),
                    onChanged: (_) =>
                        context.read<PatchingPolicyBloc>().add(const PatchingPolicyEdited()),
                  ),
                ),
                LabelledField(
                  label: 'Logo',
                  hints: const [
                    HintText(
                      'A PNG of up to ${PatchingPolicySettings.maxOrganisationLogoBytes ~/ 1024} KB, '
                      'shown beside the name.',
                    ),
                  ],
                  child: _LogoPicker(
                    logo: _logo,
                    error: _logoError ?? state.errorFor('OrganisationLogoPng'),
                    onChoose: _pickLogo,
                    onRemove: _removeLogo,
                  ),
                ),
                Align(
                  alignment: Alignment.centerLeft,
                  child: PrimaryButton(
                    label: 'Save Policy',
                    busy: state.saving,
                    onPressed: state.loading ? null : _save,
                  ),
                ),
              ],
            ),
          ],
        ),
      );
}

/// A number beside its unit — `.value-unit-row`.
class _ValueAndUnit extends StatelessWidget {
  const _ValueAndUnit({
    required this.controller,
    required this.unit,
    required this.onUnitChanged,
    required this.onEdited,
    this.errorText,
  });

  final TextEditingController controller;
  final PatchingTimeUnit unit;
  final ValueChanged<PatchingTimeUnit> onUnitChanged;
  final VoidCallback onEdited;
  final String? errorText;

  @override
  Widget build(BuildContext context) => Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SizedBox(
            width: 96,
            child: KintsugiTextField(
              controller: controller,
              keyboardType: TextInputType.number,
              errorText: errorText,
              onChanged: (_) => onEdited(),
            ),
          ),
          const SizedBox(width: 10),
          Expanded(
            child: KintsugiDropdown<PatchingTimeUnit>(
              value: unit,
              items: PatchingTimeUnit.values,
              labelOf: (value) => value.label,
              onChanged: (value) {
                onUnitChanged(value);
                onEdited();
              },
            ),
          ),
        ],
      );
}

/// The current logo, if there is one, above the buttons that change it.
class _LogoPicker extends StatelessWidget {
  const _LogoPicker({
    required this.logo,
    required this.error,
    required this.onChoose,
    required this.onRemove,
  });

  final Uint8List? logo;
  final String? error;
  final VoidCallback onChoose;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final logo = this.logo;
    final error = this.error;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (logo != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 10),
            child: Image.memory(
              logo,
              height: 48,
              fit: BoxFit.contain,
              // Passes the signature check but will not decode: say so rather than let the image
              // error reach the console, since the agent will not be able to draw it either.
              errorBuilder: (_, _, _) => const HintText('This PNG cannot be displayed.'),
            ),
          ),
        Wrap(
          spacing: 10,
          runSpacing: 10,
          children: [
            SecondaryButton(label: logo == null ? 'Choose PNG' : 'Replace', onPressed: onChoose),
            if (logo != null) SecondaryButton(label: 'Remove', onPressed: onRemove),
          ],
        ),
        if (error != null)
          Padding(
            padding: const EdgeInsets.only(top: 6),
            child: Text(
              error,
              style: Theme.of(context).textTheme.bodySmall?.copyWith(color: context.palette.red),
            ),
          ),
      ],
    );
  }
}
