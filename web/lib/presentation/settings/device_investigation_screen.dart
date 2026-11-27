import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/di/locator.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'device_investigation_cubit.dart';

/// Turns on Kintsugi's own investigation of Unstable devices, and names the Slack webhook it reports to.
/// Modelled on the Vanta screen: a write-only credential beside this server's own address. The server's
/// half is "Automatic investigation" in `src/CLAUDE.md`.
class DeviceInvestigationSettingsScreen extends StatelessWidget {
  const DeviceInvestigationSettingsScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => DeviceInvestigationCubit(
          getSettings: locator<GetDeviceInvestigationSettings>(),
          updateSettings: locator<UpdateDeviceInvestigationSettings>(),
          sendTest: locator<SendDeviceInvestigationTestAlert>(),
        )..load(),
        child: const _Form(),
      );
}

/// The address this page was opened on, as the default for the alert's link. Empty off the web (under
/// `flutter test` the base is a file: URI, whose `origin` throws).
String _pageOrigin() => Uri.base.isScheme('http') || Uri.base.isScheme('https') ? Uri.base.origin : '';

class _Form extends StatefulWidget {
  const _Form();

  @override
  State<_Form> createState() => _FormState();
}

class _FormState extends State<_Form> {
  final _webhook = TextEditingController();
  final _consoleBaseUrl = TextEditingController();
  bool _enabled = false;

  @override
  void dispose() {
    _webhook.dispose();
    _consoleBaseUrl.dispose();
    super.dispose();
  }

  void _hydrate(DeviceInvestigationSettings settings) {
    _consoleBaseUrl.text = settings.consoleBaseUrl ?? _pageOrigin();
    // Never echo the webhook back, even one just saved: it is a credential the page was never given.
    _webhook.clear();
    setState(() => _enabled = settings.enabled);
  }

  void _edited() => context.read<DeviceInvestigationCubit>().edited();

  void _save({bool clearSlackWebhook = false}) {
    final webhook = _webhook.text.trim();
    context.read<DeviceInvestigationCubit>().save(
          enabled: _enabled,
          slackWebhookUrl: clearSlackWebhook || webhook.isEmpty ? null : webhook,
          consoleBaseUrl: _consoleBaseUrl.text.trim(),
          clearSlackWebhook: clearSlackWebhook,
        );
  }

  @override
  Widget build(BuildContext context) => BlocConsumer<DeviceInvestigationCubit, DeviceInvestigationState>(
        // Also on every successful save, so a replaced webhook leaves the field even when the answer is unchanged.
        listenWhen: (previous, current) =>
            current.settings.value != null &&
            (previous.settings.value != current.settings.value || (current.settings.saved && !previous.settings.saved)),
        listener: (context, state) => _hydrate(state.settings.value!),
        builder: (context, state) {
          final settings = state.settings.value;
          final hasWebhook = settings?.hasSlackWebhook ?? false;
          final busy = state.settings.loading || state.settings.saving;

          return PageScaffold(
            title: 'Device Investigation',
            subtitle: 'When a device turns Unstable (three or more unexpected shutdowns in 7 days, or a fatal '
                'hardware error in 30), Kintsugi investigates it by itself and posts what it found to Slack.',
            children: [
              if (state.settings.error != null) AlertBox.error(state.settings.error!),
              if (state.settings.saved) const AlertBox.success('Device investigation settings saved.'),
              SettingsColumns(
                form: SettingsFormPanel(
                  maxWidth: double.infinity,
                  children: [
                    const SubHeadingTight('Investigation'),
                    KintsugiCheckbox(
                      label: 'Investigate unstable devices automatically',
                      value: _enabled,
                      onChanged: (value) {
                        setState(() => _enabled = value);
                        _edited();
                      },
                    ),
                    const SizedBox(height: 16),
                    const SubHeadingTight('Slack'),
                    LabelledField(
                      label: 'Slack webhook',
                      hints: const [
                        HintText(
                          'A Slack incoming webhook. A Google Chat space webhook works too. It is a credential: '
                          'it is never sent back to this screen.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _webhook,
                        obscureText: true,
                        hintText: hasWebhook ? 'Saved — leave blank to keep the saved one' : 'https://hooks.slack.com/services/…',
                        errorText: state.settings.errorFor('SlackWebhookUrl'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                    Wrap(spacing: 8, runSpacing: 8, children: [
                      SecondaryButton(
                        label: 'Send test message',
                        busy: state.testing,
                        tooltip: hasWebhook ? 'Sends to the saved webhook.' : 'Save a Slack webhook first.',
                        onPressed: hasWebhook && !busy ? () => context.read<DeviceInvestigationCubit>().sendTest() : null,
                      ),
                      if (hasWebhook)
                        SecondaryButton(label: 'Remove webhook', onPressed: busy ? null : () => _save(clearSlackWebhook: true)),
                    ]),
                    if (state.testSent) ...[
                      const SizedBox(height: 8),
                      const AlertBox.success('Slack accepted the test message.'),
                    ],
                    if (state.testError != null) ...[
                      const SizedBox(height: 8),
                      AlertBox.error(state.testError!),
                    ],
                    const SizedBox(height: 16),
                    const SubHeadingTight('This server'),
                    LabelledField(
                      label: "This server's address",
                      hints: const [
                        HintText(
                          'Used to link the alert to the device report, so it must be the address an '
                          'administrator opens this screen on, and https. The investigation runs on a timer, '
                          'so it cannot be worked out from a request.',
                        ),
                      ],
                      child: KintsugiTextField(
                        controller: _consoleBaseUrl,
                        hintText: 'https://kintsugi.example.com',
                        errorText: state.settings.errorFor('ConsoleBaseUrl'),
                        onChanged: (_) => _edited(),
                      ),
                    ),
                  ],
                ),
                aside: const SettingsAside(
                  title: 'What it does',
                  children: [
                    AlertBox.info('Off until an administrator turns it on.'),
                    SizedBox(height: 12),
                    HintText(
                      'Every 15 minutes Kintsugi looks for Unstable devices. For each one it asks the '
                      "device's agent for the last 7 days of operating-system warnings and errors, without "
                      "anyone asking. The log arrives at the device's next hourly check-in, and the request "
                      "is on the device's timeline as \"Kintsugi (automatic)\".",
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'The configured AI then writes the likely cause and fix from the health events, '
                      "Kintsugi's own hypotheses and excerpts of the log, with the device's name and serial "
                      'number removed. This spends the AI budget. When the AI is not configured or fails, the '
                      "report comes from Kintsugi's rules instead, and says so.",
                    ),
                    SizedBox(height: 12),
                    HintText(
                      'The report is kept on the device report page and posted to the webhook with a link to '
                      'that page. A device is investigated once every 7 days at most, unless it logs a new '
                      'fatal hardware error.',
                    ),
                  ],
                ),
              ),
              const SizedBox(height: 24),
              Align(
                alignment: Alignment.centerLeft,
                child: PrimaryButton(label: 'Save', busy: state.settings.saving, onPressed: busy ? null : _save),
              ),
            ],
          );
        },
      );
}
