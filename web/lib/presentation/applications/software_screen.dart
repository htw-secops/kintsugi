import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/software_title.dart';
import '../../domain/usecases/software_usecases.dart';
import 'software_bloc.dart';
import 'widgets/instructions_panel.dart';

/// Software titles: what Kintsugi installs, offers or removes, on which hosts — the fourth screen
/// under Applications.
///
/// A title is either a package-manager id (winget, Chocolatey, Homebrew, Flatpak, Snap), acted on
/// through that manager's one shared script, or a **custom** title whose script is researched,
/// edited, saved and signed here with the same [InstructionsPanel] the other two Applications
/// screens use. The agents act on Required and Prohibited titles silently at their hourly check-in;
/// Self-service titles appear in the tray. See "Software titles" in `src/CLAUDE.md` for the server's
/// half and `clients/CLAUDE.md` for the agents'.
class SoftwareScreen extends StatelessWidget {
  const SoftwareScreen({super.key});

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => SoftwareBloc(
          getSoftware: locator<GetSoftware>(),
          addTitle: locator<AddSoftwareTitle>(),
          updateTitle: locator<UpdateSoftwareTitle>(),
          assignTitle: locator<AssignSoftwareTitle>(),
          runNow: locator<RunSoftwareTitleNow>(),
          deleteTitle: locator<DeleteSoftwareTitle>(),
          setSecret: locator<SetScriptSecret>(),
          deleteSecret: locator<DeleteScriptSecret>(),
        )..add(const SoftwareRequested()),
        child: const _SoftwareView(),
      );
}

class _SoftwareView extends StatelessWidget {
  const _SoftwareView();

  @override
  Widget build(BuildContext context) => const BlocBuilder<SoftwareBloc, SoftwareState>(builder: _build);

  static Widget _build(BuildContext context, SoftwareState state) {
    final bloc = context.read<SoftwareBloc>();
    void reload() => bloc.add(const SoftwareRequested(showSpinner: false));

    return PageScaffold(
      title: 'Software',
      subtitle: 'Install, offer or remove software across the fleet, through a package manager or a '
          'script of your own.',
      children: [
        if (state.error case final error? when error.isNotEmpty) AlertBox.error(error),
        if (state.notice case final notice? when notice.isNotEmpty) AlertBox.success(notice),
        if (state.overview == null)
          EmptyPanel(state.loading ? 'Loading software...' : 'Could not load the software list.')
        else ...[
          const SectionHeader(
            title: 'Secrets',
            hints: [
              HintText(
                'Values a script needs and must not carry in its text — an installer key, a tenant id. '
                'A script receives each secret its title names as the environment variable '
                'KINTSUGI_SECRET_<NAME>, and nothing else does.',
              ),
              HintText(
                'A value is write-only: once saved it is never shown again, here or anywhere else. '
                'Type a new one to replace it; leave the box blank to keep the one already set.',
              ),
            ],
          ),
          _SecretsPanel(secrets: state.secrets, busy: state.busy, savedSecretName: state.savedSecretName),
          const SizedBox(height: 28),
          SectionHeader(
            title: 'Titles',
            hints: const [
              HintText(
                'Required installs a title where it is missing, Prohibited removes it where it is found, '
                'and Self-service offers it in the agent\'s tray menu. Agents act at their hourly '
                'check-in, silently and with nobody needing to be logged in; Run now asks the hosts '
                'still pending to act straight away.',
              ),
              HintText(
                'A package-manager title acts through that manager\'s one shared script, which has to be '
                'taken and signed on Upgrade Scripts before any host will run it. A custom title has a '
                'script of its own: expand the row to research, edit, save and sign it.',
              ),
              HintText(
                'Deleting a title stops Kintsugi acting on it. It removes nothing already installed '
                'and reinstalls nothing already removed.',
              ),
            ],
            actions: [
              SecondaryButton(label: 'Reload', onPressed: state.busy ? null : reload),
              PrimaryButton(
                label: 'Add Software',
                busy: state.busy,
                onPressed: () => _addSoftware(context, bloc, state.secrets),
              ),
            ],
          ),
          if (state.titles.isEmpty)
            const EmptyPanel('No software titles yet. Add one to install, offer or remove it across the fleet.')
          else
            _TitlesTable(state: state, onServerStateChanged: reload),
        ],
      ],
    );
  }

  static Future<void> _addSoftware(
    BuildContext context,
    SoftwareBloc bloc,
    List<ScriptSecretSummary> secrets,
  ) async {
    final draft = await showDialog<SoftwareTitleDraft>(
      context: context,
      builder: (_) => AddSoftwareDialog(secretNames: [for (final s in secrets) s.name]),
    );
    if (draft != null) bloc.add(SoftwareTitleAdded(draft));
  }
}

// ---------------------------------------------------------------------------------------------
// Secrets
// ---------------------------------------------------------------------------------------------

class _SecretsPanel extends StatelessWidget {
  const _SecretsPanel({required this.secrets, required this.busy, required this.savedSecretName});

  final List<ScriptSecretSummary> secrets;
  final bool busy;
  final String? savedSecretName;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          if (secrets.isNotEmpty)
            KintsugiTable(
              minWidth: 900,
              columns: [
                const TableColumnSpec(label: 'Name', width: FlexColumnWidth(1.2)),
                TableColumnSpec(
                  label: 'Value',
                  width: TableColumnSpec.forContent(StatusChip.widthFor(context, 'Not set')),
                ),
                const TableColumnSpec(label: 'Set By', width: FlexColumnWidth(1.4)),
                const TableColumnSpec(label: 'Set or Replace', width: FlexColumnWidth(2)),
                const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(110)),
              ],
              rows: [
                for (final secret in secrets)
                  KintsugiTableRow(
                    key: ValueKey('secret-${secret.name}'),
                    cells: [
                      CodeText(secret.name),
                      secret.hasValue
                          ? const StatusChip('Set', statusKey: 'up-to-date')
                          : const StatusChip('Not set', statusKey: 'review-sign'),
                      secret.updatedUtc == null && secret.updatedBy == null
                          ? const NoValue()
                          : Column(
                              crossAxisAlignment: CrossAxisAlignment.start,
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Text(secret.updatedBy ?? 'unknown'),
                                LocalTimestamp(secret.updatedUtc),
                              ],
                            ),
                      _SecretValueField(
                        name: secret.name,
                        hasValue: secret.hasValue,
                        busy: busy,
                        savedSecretName: savedSecretName,
                      ),
                      IconActionButton(
                        icon: Icons.delete_outline,
                        danger: true,
                        tooltip: 'Delete ${secret.name} (refused while a title names it)',
                        onPressed: busy ? null : () => _confirmDeleteSecret(context, secret.name),
                      ),
                    ],
                  ),
              ],
            )
          else
            const EmptyPanel('No secrets yet.'),
          const SizedBox(height: 12),
          _NewSecretForm(busy: busy, savedSecretName: savedSecretName),
        ],
      );

  static Future<void> _confirmDeleteSecret(BuildContext context, String name) async {
    final bloc = context.read<SoftwareBloc>();
    final confirmed = await _confirm(
      context,
      title: 'Delete $name?',
      message: 'Its value is discarded and cannot be recovered. The server refuses while any title still '
          'names this secret.',
      action: 'Delete',
    );
    if (confirmed) bloc.add(SoftwareSecretDeleted(name));
  }
}

/// A write-only value box: obscured while typed, cleared the moment the server accepts it, and
/// never filled from anything the server sent — there is nothing it could be filled from.
class _SecretValueField extends StatefulWidget {
  const _SecretValueField({
    required this.name,
    required this.hasValue,
    required this.busy,
    required this.savedSecretName,
  });

  final String name;
  final bool hasValue;
  final bool busy;
  final String? savedSecretName;

  @override
  State<_SecretValueField> createState() => _SecretValueFieldState();
}

class _SecretValueFieldState extends State<_SecretValueField> {
  final _value = TextEditingController();

  @override
  void initState() {
    super.initState();
    // Enables the button as soon as something is typed. Nothing here keeps the value anywhere else.
    _value.addListener(() => setState(() {}));
  }

  @override
  void didUpdateWidget(_SecretValueField oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Cleared once the save is accepted — not when Set is pressed, so a refused value is still
    // there to correct, and not left behind afterwards to be read off the screen.
    if (widget.savedSecretName == widget.name && oldWidget.savedSecretName != widget.name) _value.clear();
  }

  @override
  void dispose() {
    _value.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Row(
        children: [
          Expanded(
            child: KintsugiTextField(
              key: ValueKey('secret-value-${widget.name}'),
              controller: _value,
              obscureText: true,
              enabled: !widget.busy,
              hintText: widget.hasValue ? '•••••••• (leave blank to keep it)' : 'Value',
            ),
          ),
          const SizedBox(width: 8),
          SecondaryButton(
            label: widget.hasValue ? 'Replace' : 'Set',
            busy: widget.busy,
            onPressed: _value.text.isEmpty
                ? null
                : () => context.read<SoftwareBloc>().add(SoftwareSecretSet(widget.name, _value.text)),
          ),
        ],
      );
}

/// Adds a secret by name and value together: a secret without a value is one no script can use,
/// and the server stores a name only as part of setting it.
class _NewSecretForm extends StatefulWidget {
  const _NewSecretForm({required this.busy, required this.savedSecretName});

  final bool busy;
  final String? savedSecretName;

  @override
  State<_NewSecretForm> createState() => _NewSecretFormState();
}

class _NewSecretFormState extends State<_NewSecretForm> {
  final _name = TextEditingController();
  final _value = TextEditingController();

  @override
  void initState() {
    super.initState();
    _name.addListener(() => setState(() {}));
    _value.addListener(() => setState(() {}));
  }

  @override
  void didUpdateWidget(_NewSecretForm oldWidget) {
    super.didUpdateWidget(oldWidget);
    final saved = widget.savedSecretName;
    if (saved != null && saved != oldWidget.savedSecretName && saved == _name.text.trim()) {
      _name.clear();
      _value.clear();
    }
  }

  @override
  void dispose() {
    _name.dispose();
    _value.dispose();
    super.dispose();
  }

  String get _trimmedName => _name.text.trim();

  bool get _nameValid => softwareSecretNamePattern.hasMatch(_trimmedName);

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const SubHeadingTight('Add a secret'),
            const SizedBox(height: 8),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SizedBox(
                  width: 280,
                  child: KintsugiTextField(
                    key: const ValueKey('new-secret-name'),
                    controller: _name,
                    enabled: !widget.busy,
                    hintText: 'NAME, e.g. CROWDSTRIKE_CID',
                    errorText: _trimmedName.isEmpty || _nameValid
                        ? null
                        : 'Upper-case letters, digits and _, starting with a letter; 64 at most.',
                  ),
                ),
                const SizedBox(width: 10),
                Expanded(
                  child: KintsugiTextField(
                    key: const ValueKey('new-secret-value'),
                    controller: _value,
                    obscureText: true,
                    enabled: !widget.busy,
                    hintText: 'Value',
                  ),
                ),
                const SizedBox(width: 10),
                SecondaryButton(
                  label: 'Add Secret',
                  busy: widget.busy,
                  onPressed: !_nameValid || _value.text.isEmpty
                      ? null
                      : () => context.read<SoftwareBloc>().add(SoftwareSecretSet(_trimmedName, _value.text)),
                ),
              ],
            ),
          ],
        ),
      );
}

// ---------------------------------------------------------------------------------------------
// Titles
// ---------------------------------------------------------------------------------------------

class _TitlesTable extends StatelessWidget {
  const _TitlesTable({required this.state, required this.onServerStateChanged});

  final SoftwareState state;
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<SoftwareBloc>();
    final chipWidth = [
      for (final s in SoftwareScriptState.values) StatusChip.widthFor(context, s.label),
    ].reduce((a, b) => a > b ? a : b);
    final modeWidth = [
      for (final m in SoftwareMode.values) StatusChip.widthFor(context, m.label),
    ].reduce((a, b) => a > b ? a : b);

    return KintsugiTable(
      // Eleven columns, four of them counts. Below this the Assigned cell's "All Windows hosts"
      // wraps and the two chip columns are the next to give.
      minWidth: 1250,
      columns: [
        const TableColumnSpec(label: 'Name', width: FlexColumnWidth(1.6)),
        const TableColumnSpec(label: 'Source', width: FlexColumnWidth(1.3)),
        const TableColumnSpec(label: 'Platform', width: FixedColumnWidth(100)),
        TableColumnSpec(label: 'Mode', width: TableColumnSpec.forContent(modeWidth)),
        const TableColumnSpec(label: 'Assigned', width: FlexColumnWidth(1.1)),
        const TableColumnSpec(label: 'Compliant', width: FixedColumnWidth(96), alignRight: true),
        const TableColumnSpec(label: 'Pending', width: FixedColumnWidth(86), alignRight: true),
        const TableColumnSpec(label: 'Failed', width: FixedColumnWidth(80), alignRight: true),
        const TableColumnSpec(label: 'N/A', width: FixedColumnWidth(70), alignRight: true),
        TableColumnSpec(label: 'Script', width: TableColumnSpec.forContent(chipWidth)),
        const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(100)),
      ],
      rows: [
        for (final title in state.titles)
          KintsugiTableRow(
            // Keyed: a delete or an add reorders the rows under an open panel, and that panel holds
            // text fields that must follow their own title.
            key: ValueKey('title-${title.id}'),
            cells: [
              Text(title.name),
              title.isCustom
                  ? const Text('Custom')
                  : Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(title.sourceLabel),
                        if (title.packageId case final id?) CodeText(id, size: 11.5, muted: true),
                      ],
                    ),
              Text(title.operatingSystem),
              title.mode == null
                  ? const StatusChip('Unknown', statusKey: 'unknown')
                  : StatusChip(title.mode!.label, statusKey: _modeStatusKey(title.mode!)),
              Text(_assignedLabel(title)),
              _Count(title.compliantCount, key: ValueKey('compliant-${title.id}')),
              _Count(title.pendingCount, key: ValueKey('pending-${title.id}')),
              _Count(title.failedCount, alert: true, key: ValueKey('failed-${title.id}')),
              _Count(title.notApplicableCount, key: ValueKey('na-${title.id}')),
              title.scriptState == null
                  ? const StatusChip('Unknown', statusKey: 'unknown')
                  : StatusChip(title.scriptState!.label, statusKey: title.scriptState!.statusKey),
              IconActionButton(
                icon: state.expandedId == title.id ? Icons.expand_less : Icons.expand_more,
                tooltip: 'Edit ${title.name}',
                onPressed: () => bloc.add(SoftwareRowToggled(title.id)),
              ),
            ],
            expanded: state.expandedId == title.id
                ? _TitlePanel(
                    key: ValueKey('panel-${title.id}'),
                    title: title,
                    secrets: state.secrets,
                    busy: state.busy,
                    onServerStateChanged: onServerStateChanged,
                  )
                : null,
          ),
      ],
    );
  }

  /// Prohibited is the one that removes things, so it takes the amber of something to watch;
  /// the other two are a plain fact about the row.
  static String _modeStatusKey(SoftwareMode mode) => switch (mode) {
        SoftwareMode.prohibited => 'update-available',
        _ => 'os-update',
      };

  static String _assignedLabel(SoftwareTitle title) {
    final hosts = title.assignedHostNames.length;
    final named = switch (hosts) { 0 => '', 1 => '1 host', _ => '$hosts hosts' };
    if (title.assignedToAll) return hosts == 0 ? 'All ${title.operatingSystem} hosts' : 'All, plus $named';
    return hosts == 0 ? 'Nobody' : named;
  }
}

class _Count extends StatelessWidget {
  const _Count(this.count, {super.key, this.alert = false});

  final int count;
  final bool alert;

  @override
  Widget build(BuildContext context) =>
      Align(alignment: Alignment.centerRight, child: CountBadge(count, alert: alert && count > 0));
}

/// What a title's row opens into: its settings, its assignment, Run now and Delete, then its script.
class _TitlePanel extends StatelessWidget {
  const _TitlePanel({
    super.key,
    required this.title,
    required this.secrets,
    required this.busy,
    required this.onServerStateChanged,
  });

  final SoftwareTitle title;
  final List<ScriptSecretSummary> secrets;
  final bool busy;
  final VoidCallback onServerStateChanged;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            spacing: 16,
            runSpacing: 16,
            children: [
              SizedBox(width: 460, child: _SettingsForm(title: title, secrets: secrets, busy: busy)),
              SizedBox(width: 460, child: _AssignmentForm(title: title, busy: busy)),
            ],
          ),
          const SizedBox(height: 16),
          _TitleActions(title: title, busy: busy),
          const SizedBox(height: 20),
          if (title.isCustom)
            InstructionsPanel(
              key: ValueKey('instructions-${title.id}'),
              applicationName: title.name,
              platform: title.platform,
              onServerStateChanged: onServerStateChanged,
            )
          else
            _ManagerScriptNote(title: title),
        ],
      );
}

/// Mode, detect-as and the secrets the title's script is given.
class _SettingsForm extends StatefulWidget {
  const _SettingsForm({required this.title, required this.secrets, required this.busy});

  final SoftwareTitle title;
  final List<ScriptSecretSummary> secrets;
  final bool busy;

  @override
  State<_SettingsForm> createState() => _SettingsFormState();
}

class _SettingsFormState extends State<_SettingsForm> {
  late SoftwareMode? _mode = widget.title.mode;
  late final _detectAs = TextEditingController(text: widget.title.detectAs ?? '');
  late Set<String> _secretNames = {...widget.title.secretNames};

  @override
  void didUpdateWidget(_SettingsForm oldWidget) {
    super.didUpdateWidget(oldWidget);
    // Adopt what the server now holds after a save, so the form never shows a value it did not keep.
    if (oldWidget.title != widget.title) {
      _mode = widget.title.mode;
      if (_detectAs.text != (widget.title.detectAs ?? '')) _detectAs.text = widget.title.detectAs ?? '';
      _secretNames = {...widget.title.secretNames};
    }
  }

  @override
  void dispose() {
    _detectAs.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // A secret the title names that has since disappeared from the list is still shown, so saving
    // never drops it silently — the server is what says it no longer exists.
    final available = {for (final s in widget.secrets) s.name, ...widget.title.secretNames}.toList()..sort();

    return KintsugiPanel(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const SubHeadingTight('What it wants'),
          const SizedBox(height: 10),
          LabelledField(
            label: 'Mode',
            hints: [if (_mode case final mode?) HintText(mode.description)],
            child: KintsugiDropdown<SoftwareMode?>(
              value: _mode,
              // A mode this client does not know shows as an empty select, and Save stays disabled
              // until one is chosen — never a guess that a save would then make true.
              items: <SoftwareMode?>[...SoftwareMode.values],
              labelOf: (mode) => mode?.label ?? '',
              onChanged: widget.busy ? null : (mode) => setState(() => _mode = mode),
            ),
          ),
          const SizedBox(height: 12),
          LabelledField(
            label: 'Detected as',
            hints: const [
              HintText(
                'Optional. Counts the title as installed when an installed application\'s name starts '
                'with this — for software that reports itself under a different name.',
              ),
            ],
            child: KintsugiTextField(controller: _detectAs, enabled: !widget.busy),
          ),
          const SizedBox(height: 12),
          LabelledField(
            label: 'Secrets',
            hints: const [HintText('The script receives each one ticked here as KINTSUGI_SECRET_<NAME>.')],
            child: available.isEmpty
                ? const HintText('No secrets exist yet. Add one above first.')
                : _SecretPicker(
                    available: available,
                    selected: _secretNames,
                    enabled: !widget.busy,
                    onChanged: (names) => setState(() => _secretNames = names),
                  ),
          ),
          const SizedBox(height: 14),
          PrimaryButton(
            label: 'Save',
            busy: widget.busy,
            onPressed: _mode == null
                ? null
                : () {
                    final detectAs = _detectAs.text.trim();
                    context.read<SoftwareBloc>().add(SoftwareTitleSaved(
                          widget.title.id,
                          mode: _mode!,
                          detectAs: detectAs.isEmpty ? null : detectAs,
                          secretNames: _secretNames.toList()..sort(),
                        ));
                  },
          ),
        ],
      ),
    );
  }
}

class _SecretPicker extends StatelessWidget {
  const _SecretPicker({
    required this.available,
    required this.selected,
    required this.enabled,
    required this.onChanged,
  });

  final List<String> available;
  final Set<String> selected;
  final bool enabled;
  final ValueChanged<Set<String>> onChanged;

  @override
  Widget build(BuildContext context) => Wrap(
        spacing: 8,
        runSpacing: 8,
        children: [
          for (final name in available)
            FilterChip(
              label: Text(name),
              selected: selected.contains(name),
              onSelected: !enabled
                  ? null
                  : (on) => onChanged(on ? {...selected, name} : ({...selected}..remove(name))),
            ),
        ],
      );
}

/// Every host of the title's operating system, a list of host names, or both.
///
/// The Software screen's own form rather than Baselines' `_AssignmentForm`, which is private to
/// `baseline_screen.dart` and is being changed under IP-875 — when that work leaves a shared
/// assignment widget behind, this is the one to replace with it.
class _AssignmentForm extends StatefulWidget {
  const _AssignmentForm({required this.title, required this.busy});

  final SoftwareTitle title;
  final bool busy;

  @override
  State<_AssignmentForm> createState() => _AssignmentFormState();
}

class _AssignmentFormState extends State<_AssignmentForm> {
  late bool _allHosts = widget.title.assignedToAll;
  late final _hostNames = TextEditingController(text: widget.title.assignedHostNames.join('\n'));

  @override
  void didUpdateWidget(_AssignmentForm oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.title.assignedToAll != widget.title.assignedToAll ||
        oldWidget.title.assignedHostNames.join('\n') != widget.title.assignedHostNames.join('\n')) {
      _allHosts = widget.title.assignedToAll;
      _hostNames.text = widget.title.assignedHostNames.join('\n');
    }
  }

  @override
  void dispose() {
    _hostNames.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const SubHeadingTight('Applies to'),
            const SizedBox(height: 10),
            KintsugiCheckbox(
              label: 'Every ${widget.title.operatingSystem} host',
              value: _allHosts,
              onChanged: (value) {
                if (!widget.busy) setState(() => _allHosts = value);
              },
            ),
            const SizedBox(height: 12),
            LabelledField(
              label: 'And these hosts',
              hints: [
                HintText(
                  'One host name per line. A host that is not a ${widget.title.operatingSystem} host is '
                  'refused, and nothing is saved.',
                ),
                if (!widget.title.isCustom)
                  const HintText('A host without the package manager counts as N/A, never as failed.'),
              ],
              child: KintsugiTextField(
                key: ValueKey('assignment-hosts-${widget.title.id}'),
                controller: _hostNames,
                minLines: 3,
                maxLines: 8,
                enabled: !widget.busy,
              ),
            ),
            const SizedBox(height: 12),
            PrimaryButton(
              label: 'Save Assignment',
              busy: widget.busy,
              onPressed: () => context.read<SoftwareBloc>().add(SoftwareAssignmentSaved(
                    widget.title.id,
                    allHosts: _allHosts,
                    hostNames: [
                      for (final line in _hostNames.text.split('\n'))
                        if (line.trim().isNotEmpty) line.trim(),
                    ],
                  )),
            ),
          ],
        ),
      );
}

class _TitleActions extends StatelessWidget {
  const _TitleActions({required this.title, required this.busy});

  final SoftwareTitle title;
  final bool busy;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<SoftwareBloc>();
    final reason = _runNowBlocked(title);

    return Row(
      children: [
        SecondaryButton(
          label: 'Run Now',
          busy: busy,
          tooltip: reason ??
              'Ask the ${title.pendingCount} pending host(s) to act within the minute rather than at '
                  'their next hourly check-in. Silent: no prompt and no progress window.',
          onPressed: reason == null ? () => bloc.add(SoftwareRunNowRequested(title.id)) : null,
        ),
        const SizedBox(width: 12),
        SecondaryButton(
          label: 'Delete',
          busy: busy,
          tooltip: 'Stop acting on ${title.name}. Removes nothing already installed.',
          onPressed: () async {
            final confirmed = await _confirm(
              context,
              title: 'Delete ${title.name}?',
              message: 'Kintsugi stops installing, offering or removing it. Nothing already installed is '
                  'removed, and nothing already removed comes back.',
              action: 'Delete',
            );
            if (confirmed) bloc.add(SoftwareTitleDeleted(title.id));
          },
        ),
      ],
    );
  }

  /// Why Run now would instruct hosts to do nothing — said on the disabled button rather than
  /// leaving an enabled one that quietly achieves nothing, as "Patch now" does.
  static String? _runNowBlocked(SoftwareTitle title) {
    if (!title.isAssigned) return 'Assign it to some hosts first.';
    if (title.scriptState != SoftwareScriptState.ready) {
      return 'Its script is not ready, so no host would act. See the note below.';
    }
    if (title.mode == SoftwareMode.selfService) {
      return 'Self-service titles are offered in the tray, never installed unasked.';
    }
    if (title.pendingCount == 0) return 'No host is pending.';
    return null;
  }
}

/// A package-manager title's script is the manager's own, shared by every title and every upgrade
/// that manager handles. It is signed once on Upgrade Scripts, never here — and the server never
/// replaces it automatically (see "Software titles" in `src/CLAUDE.md`).
class _ManagerScriptNote extends StatelessWidget {
  const _ManagerScriptNote({required this.title});

  final SoftwareTitle title;

  @override
  Widget build(BuildContext context) {
    final manager = title.packageManager!;
    final bucket = title.managerBucket!;
    final state = title.scriptState;
    final opener = 'Acts through the $manager script ($bucket), the one script every $manager title and '
        'upgrade shares.';

    final needsSigning = state == SoftwareScriptState.noActionMode || state == SoftwareScriptState.unsigned;
    final message = switch (state) {
      SoftwareScriptState.ready => '$opener It is signed and supports install and uninstall.',
      SoftwareScriptState.noActionMode => '$opener The signed version predates install and uninstall, so '
          'no host will act on this title. Take + Sign the $manager script on Upgrade Scripts.',
      SoftwareScriptState.unsigned => '$opener It is not signed, so no host will run it. Take + Sign the '
          '$manager script on Upgrade Scripts.',
      SoftwareScriptState.missing => '$opener The server holds no $manager script yet. Take + Sign it on '
          'Upgrade Scripts.',
      null => '$opener Its state is one this screen does not recognise; check it on Upgrade Scripts.',
    };

    final link = LinkText(label: 'Open Upgrade Scripts', onTap: () => context.go(Routes.upgradeScripts));
    return needsSigning || state == SoftwareScriptState.missing
        ? AlertBox.error(message, child: link)
        : AlertBox.info(message, child: link);
  }
}

// ---------------------------------------------------------------------------------------------
// Add Software
// ---------------------------------------------------------------------------------------------

/// The Add Software dialog. Pops a [SoftwareTitleDraft], or null when cancelled.
class AddSoftwareDialog extends StatefulWidget {
  const AddSoftwareDialog({super.key, required this.secretNames});

  final List<String> secretNames;

  @override
  State<AddSoftwareDialog> createState() => _AddSoftwareDialogState();
}

class _AddSoftwareDialogState extends State<AddSoftwareDialog> {
  static const _custom = 'Custom';

  final _name = TextEditingController();
  final _packageId = TextEditingController();
  final _detectAs = TextEditingController();
  String _source = softwarePackageManagers.keys.first;
  String _platform = softwareOperatingSystems.first;
  SoftwareMode _mode = SoftwareMode.required;
  Set<String> _secretNames = {};

  @override
  void initState() {
    super.initState();
    _name.addListener(() => setState(() {}));
    _packageId.addListener(() => setState(() {}));
  }

  @override
  void dispose() {
    _name.dispose();
    _packageId.dispose();
    _detectAs.dispose();
    super.dispose();
  }

  bool get _isCustom => _source == _custom;

  bool get _complete => _name.text.trim().isNotEmpty && (_isCustom || _packageId.text.trim().isNotEmpty);

  SoftwareTitleDraft _draft() {
    final detectAs = _detectAs.text.trim();
    return SoftwareTitleDraft(
      name: _name.text.trim(),
      packageManager: _isCustom ? null : _source,
      platform: _isCustom ? _platform : null,
      packageId: _isCustom ? null : _packageId.text.trim(),
      detectAs: detectAs.isEmpty ? null : detectAs,
      mode: _mode,
      secretNames: _secretNames.toList()..sort(),
    );
  }

  @override
  Widget build(BuildContext context) => AlertDialog(
        backgroundColor: context.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: context.palette.border),
        ),
        title: Text('Add Software', style: Theme.of(context).textTheme.titleLarge),
        content: SizedBox(
          width: 560,
          child: SingleChildScrollView(
            child: Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                LabelledField(
                  label: 'Name',
                  hints: const [
                    HintText(
                      'Use the name Currently Installed shows for it, so it is recognised once installed '
                      'and its later upgrades resolve to the same script.',
                    ),
                  ],
                  child: KintsugiTextField(key: const ValueKey('add-name'), controller: _name),
                ),
                const SizedBox(height: 12),
                LabelledField(
                  label: 'Source',
                  hints: const [
                    HintText(
                      'A package manager acts through its one shared script. Custom means a script of '
                      'this title\'s own, researched or written after it is added.',
                    ),
                  ],
                  child: KintsugiDropdown<String>(
                    key: const ValueKey('add-source'),
                    value: _source,
                    items: [...softwarePackageManagers.keys, _custom],
                    labelOf: (source) => source == _custom
                        ? 'Custom script'
                        : '$source (${softwarePackageManagers[source]})',
                    onChanged: (source) => setState(() => _source = source),
                  ),
                ),
                const SizedBox(height: 12),
                if (_isCustom)
                  LabelledField(
                    label: 'Platform',
                    child: KintsugiDropdown<String>(
                      key: const ValueKey('add-platform'),
                      value: _platform,
                      items: softwareOperatingSystems,
                      onChanged: (platform) => setState(() => _platform = platform),
                    ),
                  )
                else
                  LabelledField(
                    label: 'Package id',
                    hints: [HintText(_packageIdHint(_source))],
                    child: KintsugiTextField(key: const ValueKey('add-package-id'), controller: _packageId),
                  ),
                const SizedBox(height: 12),
                LabelledField(
                  label: 'Detected as',
                  hints: const [
                    HintText('Optional. Counts it as installed when an installed application\'s name starts with this.'),
                  ],
                  child: KintsugiTextField(key: const ValueKey('add-detect-as'), controller: _detectAs),
                ),
                const SizedBox(height: 12),
                LabelledField(
                  label: 'Mode',
                  hints: [HintText(_mode.description)],
                  child: KintsugiDropdown<SoftwareMode>(
                    key: const ValueKey('add-mode'),
                    value: _mode,
                    items: SoftwareMode.values,
                    labelOf: (mode) => mode.label,
                    onChanged: (mode) => setState(() => _mode = mode),
                  ),
                ),
                const SizedBox(height: 12),
                LabelledField(
                  label: 'Secrets',
                  child: widget.secretNames.isEmpty
                      ? const HintText('None exist yet. Add them under Secrets; a title can be given them later.')
                      : _SecretPicker(
                          available: widget.secretNames,
                          selected: _secretNames,
                          enabled: true,
                          onChanged: (names) => setState(() => _secretNames = names),
                        ),
                ),
              ],
            ),
          ),
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(context).pop()),
          PrimaryButton(
            label: 'Add',
            onPressed: _complete ? () => Navigator.of(context).pop(_draft()) : null,
          ),
        ],
      );

  static String _packageIdHint(String manager) => switch (manager) {
        'winget' => 'The winget id, matched exactly — e.g. Greenshot.Greenshot.',
        'Chocolatey' => 'The Chocolatey package name — e.g. 7zip.',
        'Homebrew' => 'The formula or cask name — e.g. firefox.',
        'Flatpak' => 'The Flatpak application id — e.g. org.mozilla.firefox.',
        'Snap' => 'The snap name — e.g. firefox.',
        _ => 'The id the package manager knows it by.',
      };
}

// ---------------------------------------------------------------------------------------------

Future<bool> _confirm(
  BuildContext context, {
  required String title,
  required String message,
  required String action,
}) async =>
    await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.backgroundAlt,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text(title, style: Theme.of(dialogContext).textTheme.titleLarge),
        content: HintText(message),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: action, onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    ) ??
    false;
