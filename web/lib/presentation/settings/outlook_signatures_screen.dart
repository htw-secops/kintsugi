import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'outlook_signatures_bloc.dart';
import 'settings_state.dart';

/// Where managed Outlook signatures come from (`OutlookSignatureSourceSettings`), and whether the hourly
/// pull is working. The signatures themselves reach a PC through a baseline's `outlook-signature` item;
/// this screen only decides what the server holds to hand out.
class OutlookSignaturesSettingsScreen extends StatelessWidget {
  const OutlookSignaturesSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => OutlookSignaturesBloc(
          getSource: locator<GetOutlookSignatureSource>(),
          updateSource: locator<UpdateOutlookSignatureSource>(),
        )..add(const OutlookSignaturesRequested()),
        child: const _OutlookSignaturesForm(),
      );
}

class _OutlookSignaturesForm extends StatefulWidget {
  const _OutlookSignaturesForm();

  @override
  State<_OutlookSignaturesForm> createState() => _OutlookSignaturesFormState();
}

class _OutlookSignaturesFormState extends State<_OutlookSignaturesForm> {
  final _endpointUrl = TextEditingController();
  final _audience = TextEditingController();
  final _suffix = TextEditingController();
  bool _enabled = false;

  @override
  void dispose() {
    _endpointUrl.dispose();
    _audience.dispose();
    _suffix.dispose();
    super.dispose();
  }

  void _hydrate(OutlookSignatureSource source) {
    _endpointUrl.text = source.endpointUrl ?? '';
    _audience.text = source.audience ?? '';
    _suffix.text = source.windowsUsernameSuffix;
    setState(() => _enabled = source.enabled);
  }

  void _edited() => context.read<OutlookSignaturesBloc>().add(const OutlookSignaturesEdited());

  void _save() => context.read<OutlookSignaturesBloc>().add(OutlookSignaturesSaveRequested(
        enabled: _enabled,
        endpointUrl: _endpointUrl.text.trim(),
        audience: _audience.text.trim(),
        windowsUsernameSuffix: _suffix.text.trim(),
      ));

  Widget _text(TextEditingController controller, SettingsState<OutlookSignatureSource> state, String property,
          {String? hintText}) =>
      KintsugiTextField(
        controller: controller,
        hintText: hintText,
        errorText: state.errorFor(property),
        onChanged: (_) => _edited(),
      );

  @override
  Widget build(BuildContext context) =>
      BlocConsumer<OutlookSignaturesBloc, SettingsState<OutlookSignatureSource>>(
        // Hydrate on a changed *setting*, not on every read: "Refresh Status" re-reads the same settings
        // with a new pull time, and must not throw away an edit in progress.
        listenWhen: (previous, current) =>
            current.value != null && !_sameSettings(previous.value, current.value!),
        listener: (context, state) => _hydrate(state.value!),
        builder: (context, state) => PageScaffold(
          title: 'Outlook Signatures',
          subtitle: 'Where managed Outlook signatures are pulled from. The server fetches every '
              'person\'s copy each hour; a baseline\'s outlook-signature item then writes it on each PC, '
              'for whoever is signed in.',
          children: [
            if (state.error != null) AlertBox.error(state.error!),
            if (state.saved) const AlertBox.success('Settings saved.'),
            SettingsColumns(
              form: SettingsFormPanel(
                children: [
                  KintsugiCheckbox(
                    label: 'Pull signatures from this source',
                    value: _enabled,
                    onChanged: (value) {
                      setState(() => _enabled = value);
                      _edited();
                    },
                  ),
                  const HintText(
                    'Switching this off stops the pull and keeps the copies already held. Only the '
                    'source answering "disabled" removes them.',
                  ),
                  LabelledField(
                    label: 'Endpoint URL',
                    hints: const [
                      HintText('The source\'s bulk read. https only: this server sends it an ID token for its own identity.'),
                    ],
                    child: _text(_endpointUrl, state, 'EndpointUrl', hintText: 'https://signatures.example.com/bulk'),
                  ),
                  LabelledField(
                    label: 'Audience',
                    hints: const [HintText('What the ID token is minted for. The source checks it.')],
                    child: _text(_audience, state, 'Audience'),
                  ),
                  LabelledField(
                    label: 'Windows username suffix',
                    hints: const [
                      HintText(
                        'Added to an email\'s local part to recognise a Windows username when a person '
                        'has no Google Credential Provider record. Blank for none.',
                      ),
                    ],
                    child: _text(_suffix, state, 'WindowsUsernameSuffix'),
                  ),
                  Align(
                    alignment: Alignment.centerLeft,
                    child: PrimaryButton(
                      label: 'Save Settings',
                      busy: state.saving,
                      onPressed: state.loading ? null : _save,
                    ),
                  ),
                ],
              ),
              aside: _PullStatus(source: state.value, loading: state.loading),
            ),
          ],
        ),
      );

  static bool _sameSettings(OutlookSignatureSource? a, OutlookSignatureSource b) =>
      a != null &&
      a.enabled == b.enabled &&
      a.endpointUrl == b.endpointUrl &&
      a.audience == b.audience &&
      a.windowsUsernameSuffix == b.windowsUsernameSuffix;
}

/// The last pull, read-only. A failing pull keeps the previous copies, so "Error" beside a non-zero copy
/// count means PCs are still getting yesterday's signatures, not none.
class _PullStatus extends StatelessWidget {
  const _PullStatus({required this.source, required this.loading});

  final OutlookSignatureSource? source;
  final bool loading;

  @override
  Widget build(BuildContext context) {
    final source = this.source;
    return SettingsAside(
      title: 'Last pull',
      children: [
        if (source == null)
          const HintText('Not read yet.')
        else ...[
          _row('Status', _statusChip(source)),
          _row('When', source.lastPullUtc == null ? const HintText('Never') : LocalTimestamp(source.lastPullUtc)),
          _row('Count', source.lastPullCount == null ? const NoValue() : Text('${source.lastPullCount}')),
          _row('Copies held', Text('${source.copyCount}')),
          if (source.lastError != null) ...[
            const SizedBox(height: 8),
            AlertBox.error(source.lastError!),
          ],
          if (!source.isConfigured) ...[
            const SizedBox(height: 8),
            const HintText('Not configured: the pull needs an endpoint URL and an audience.'),
          ],
          const SizedBox(height: 8),
          const HintText(
            'The pull runs about a minute after the server starts and then hourly, so a saved change '
            'takes effect within the hour.',
          ),
        ],
        const SizedBox(height: 12),
        SecondaryButton(
          label: 'Refresh Status',
          busy: loading,
          onPressed: () => context.read<OutlookSignaturesBloc>().add(const OutlookSignaturesRequested()),
        ),
      ],
    );
  }

  static Widget _statusChip(OutlookSignatureSource source) => switch (source.lastPullStatus) {
        'ok' => const StatusChip('OK', statusKey: 'up-to-date'),
        'disabled' => const StatusChip('Disabled by source', statusKey: 'mapping-not-applicable'),
        'error' => const StatusChip('Error', statusKey: 'patch-failed'),
        null => const HintText('No pull yet'),
        final other => Text(other),
      };

  static Widget _row(String label, Widget value) => Padding(
        padding: const EdgeInsets.only(bottom: 6),
        child: Row(
          children: [
            SizedBox(width: 110, child: HintText(label)),
            Flexible(child: value),
          ],
        ),
      );
}
