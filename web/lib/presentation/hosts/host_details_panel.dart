import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';
import 'host_os_update.dart';
import 'hosts_format.dart';

/// What an administrator can do to a host from its details panel.
enum HostPanelAction { terminal, connect, remove, hostPage }

/// Opens the right-hand details panel for [host].
///
/// A sliding panel rather than a page: the table stays where it was underneath, so looking at one host
/// never costs the filters and the scroll position the administrator had. [onAction] is called *after*
/// the panel has closed, so whatever it does (navigate, ask to confirm a removal) starts from the page.
Future<void> showHostDetails(
  BuildContext context, {
  required HostSummary host,
  required void Function(HostPanelAction action) onAction,
}) async {
  final action = await showGeneralDialog<HostPanelAction>(
    context: context,
    barrierDismissible: true,
    barrierLabel: 'Close details',
    barrierColor: Colors.black.withValues(alpha: 0.45),
    transitionDuration: const Duration(milliseconds: 160),
    pageBuilder: (dialogContext, _, _) => Align(
      alignment: Alignment.centerRight,
      child: SizedBox(width: 440, height: double.infinity, child: HostDetailsPanel(host: host)),
    ),
    transitionBuilder: (context, animation, _, child) => SlideTransition(
      position: Tween(begin: const Offset(1, 0), end: Offset.zero)
          .animate(CurvedAnimation(parent: animation, curve: Curves.easeOutCubic)),
      child: child,
    ),
  );
  if (action != null) onAction(action);
}

/// The details of one host: its facts, and the person it is assigned to.
///
/// **The person is shown as the short name only until Reveal is pressed.** The list the server sends
/// never carries the full name or the email; pressing Reveal asks for them with one request, which the
/// server records on the host's activity timeline with the administrator's name. That timeline is
/// pruned by the host activity retention setting and goes with the host when it is removed, so the
/// panel says "activity timeline", never "audit log". The request is made on the press and never
/// earlier, never twice for one panel, and never to pre-load.
class HostDetailsPanel extends StatefulWidget {
  const HostDetailsPanel({super.key, required this.host});

  final HostSummary host;

  @override
  State<HostDetailsPanel> createState() => _HostDetailsPanelState();
}

class _HostDetailsPanelState extends State<HostDetailsPanel> {
  AssignedPerson? _revealed;
  String? _revealedAt;
  bool _revealing = false;
  bool _clearing = false;

  /// The person was removed from this panel. The list behind it still has the old short name until its
  /// next poll, so the panel goes by this rather than by [HostSummary.hasAssignedPerson].
  bool _cleared = false;
  String? _error;

  /// Maintenance mode as this panel last set it. The list behind the panel has the old state until its next poll,
  /// so once a button here has been pressed the panel goes by this rather than by the [HostSummary].
  bool? _maintenance;
  bool _savingMaintenance = false;

  Future<void> _startMaintenance() async {
    final choice = await showDialog<_MaintenanceChoice>(
      context: context,
      builder: (_) => const _MaintenanceDialog(),
    );
    if (choice == null || !mounted) return;
    await _saveMaintenance(
      () => locator<SetHostMaintenance>().start(
        widget.host.id,
        until: choice.hours == null ? null : DateTime.now().toUtc().add(Duration(hours: choice.hours!)),
        reason: choice.reason,
      ),
      on: true,
    );
  }

  Future<void> _endMaintenance() =>
      _saveMaintenance(() => locator<SetHostMaintenance>().end(widget.host.id), on: false);

  Future<void> _saveMaintenance(Future<void> Function() call, {required bool on}) async {
    setState(() {
      _savingMaintenance = true;
      _error = null;
    });
    try {
      await call();
      if (mounted) setState(() => _maintenance = on);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = 'Could not change maintenance mode: ${error.message}');
    } finally {
      if (mounted) setState(() => _savingMaintenance = false);
    }
  }

  Future<void> _reveal() async {
    setState(() {
      _revealing = true;
      _error = null;
    });
    try {
      final person = await locator<RevealAssignedPerson>()(widget.host.id);
      if (!mounted) return;
      setState(() {
        _revealed = person;
        _revealedAt = DateFormat('HH:mm').format(DateTime.now());
      });
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = 'Could not reveal: ${error.message}');
    } finally {
      if (mounted) setState(() => _revealing = false);
    }
  }

  Future<void> _clear() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.surface,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Remove the name and email?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: const HintText(
          "They are deleted from Kintsugi for this host, and the removal is recorded on the host's activity "
          'timeline with your name. An import with the name or email ticked can bring them back.',
        ),
        actions: [
          SecondaryButton(
            key: const Key('clear-person-cancel'),
            label: 'Cancel',
            onPressed: () => Navigator.of(dialogContext).pop(false),
          ),
          PrimaryButton(
            key: const Key('clear-person-confirm'),
            label: 'Remove',
            onPressed: () => Navigator.of(dialogContext).pop(true),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    setState(() {
      _clearing = true;
      _error = null;
    });
    try {
      await locator<ClearAssignedPerson>()(widget.host.id);
      if (!mounted) return;
      setState(() {
        _cleared = true;
        _revealed = null;
      });
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = 'Could not remove: ${error.message}');
    } finally {
      if (mounted) setState(() => _clearing = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final host = widget.host;
    final revealed = _revealed;

    return Material(
      color: palette.surface,
      shape: Border(left: BorderSide(color: palette.border)),
      child: SingleChildScrollView(
        padding: const EdgeInsets.fromLTRB(22, 20, 22, 24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Expanded(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(host.hostname, style: AppTheme.label(color: palette.text, size: 15,)),
                      const SizedBox(height: 3),
                      HintText([
                        if (host.model != null) host.model!,
                        if (host.operatingSystem != null) host.operatingSystem!,
                      ].join(' · ')),
                    ],
                  ),
                ),
                IconButton(
                  key: const Key('details-close'),
                  tooltip: 'Close',
                  onPressed: () => Navigator.of(context).pop(),
                  icon: const Icon(Icons.close),
                ),
              ],
            ),
            const SizedBox(height: 16),
            _Section(
              title: 'Device',
              children: [
                _Fact(label: 'Hostname', value: host.hostname, copy: true, copyKey: const Key('details-copy-hostname')),
                _Fact(label: 'Serial', value: host.serialNumber, copy: true, copyKey: const Key('details-copy-serial')),
                _Fact(label: 'Department', value: host.department),
                _Fact(label: 'Location', value: host.location),
                _Fact(
                  label: 'Health',
                  child: Row(
                    children: [
                      StatusChip(host.health.label, statusKey: hostHealthChipKey(host.health)),
                      if (host.healthReason != null) ...[
                        const SizedBox(width: 8),
                        Flexible(child: HintText(host.healthReason!)),
                      ],
                      if (host.unstable) ...[
                        const SizedBox(width: 8),
                        const UnstableBadge(),
                      ],
                    ],
                  ),
                ),
                _Fact(
                  label: 'Last uptime',
                  value: host.isConnected ? 'Connected' : hostRelativeAgo(host.lastSeenUtc),
                ),
                // What the table has no column for any more. The OS update state is the old column's
                // own chip, latest-version text and restart tag.
                _Fact(label: 'OS update', child: HostOsUpdate(host: host)),
                _Fact(label: 'IP address', value: host.ipAddress, mono: true),
                _Fact(label: 'Agent version', value: host.agentVersion),
                host.failedPatchCount > 0
                    ? _Fact(
                        label: 'Failed patches',
                        child: FailedPatchesLink(hostname: host.hostname, text: '${host.failedPatchCount}'),
                      )
                    : const _Fact(label: 'Failed patches', value: '0'),
                _Fact(label: 'App updates', value: '${host.appUpdatesAvailableCount}'),
                _Fact(label: 'CVEs not fixed', value: '${host.unpatchedCveCount}'),
                _Fact(label: 'CVEs up to date', value: '${host.patchedCveCount}'),
              ],
            ),
            const SizedBox(height: 12),
            _Section(
              title: 'Maintenance mode',
              children: [
                if (_maintenance ?? host.inMaintenance) ...[
                  HintText(
                    _maintenance == null && host.maintenanceUntilUtc != null
                        ? 'In maintenance until ${DateFormat('d MMM HH:mm').format(host.maintenanceUntilUtc!.toLocal())}. '
                            'Kintsugi gives this device no patch work and refuses Patch now. It stays on and keeps checking in. '
                            'Operating-system updates, baselines and agent updates still run.'
                        : 'In maintenance. Kintsugi gives this device no patch work and refuses Patch now. '
                            'It stays on and keeps checking in. Operating-system updates, baselines and agent updates still run.',
                  ),
                  if (_maintenance == null && host.maintenanceReason != null) _Fact(label: 'Reason', value: host.maintenanceReason),
                  const SizedBox(height: 8),
                  SecondaryButton(
                    key: const Key('maintenance-end'),
                    label: _savingMaintenance ? 'Ending…' : 'End maintenance',
                    onPressed: _savingMaintenance ? null : _endMaintenance,
                  ),
                ] else ...[
                  const HintText(
                    'Use it while a device is being fixed. Kintsugi stops giving it patch work and refuses Patch now. '
                    'The device stays on and keeps checking in.',
                  ),
                  const SizedBox(height: 8),
                  SecondaryButton(
                    key: const Key('maintenance-start'),
                    label: _savingMaintenance ? 'Starting…' : 'Start maintenance',
                    onPressed: _savingMaintenance ? null : _startMaintenance,
                  ),
                ],
              ],
            ),
            const SizedBox(height: 12),
            _Section(
              title: 'Assigned person',
              badge: const StatusChip('Admins only', statusKey: 'update-available'),
              children: [
                if (!host.hasAssignedPerson || _cleared) ...[
                  const _Fact(label: 'Name'),
                  const _Fact(label: 'Email'),
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: HintText(_cleared
                        ? 'The name and email were removed. The list shows it after its next refresh.'
                        : 'No name or email has been imported for this host. They are optional and '
                            'off by default when importing.'),
                  ),
                ] else ...[
                  _Fact(
                    label: 'Name',
                    value: revealed != null ? revealed.name : host.assignedShortName,
                    copy: revealed?.name != null,
                    copyKey: const Key('details-copy-name'),
                  ),
                  _Fact(
                    label: 'Email',
                    value: revealed != null ? revealed.email : '••••••@••••••',
                    mono: revealed == null,
                    copy: revealed?.email != null,
                    copyKey: const Key('details-copy-email'),
                  ),
                  const SizedBox(height: 8),
                  Wrap(
                    spacing: 8,
                    runSpacing: 8,
                    children: [
                      if (revealed == null)
                        SecondaryButton(
                          key: const Key('reveal-person'),
                          label: 'Reveal name and email',
                          busy: _revealing,
                          onPressed: _reveal,
                        ),
                      SecondaryButton(
                        key: const Key('clear-person'),
                        label: 'Remove name and email',
                        busy: _clearing,
                        onPressed: _clear,
                      ),
                    ],
                  ),
                  if (_error != null)
                    Padding(
                      padding: const EdgeInsets.only(top: 8),
                      child: Text(_error!, style: TextStyle(color: palette.red)),
                    ),
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: HintText(
                      revealed != null
                          ? "Revealed at $_revealedAt. Recorded on this host's activity timeline with your name."
                          : 'Only the first name and the initial of the surname are shown. The full name and '
                              'the email stay hidden until you reveal them, and every reveal is recorded on this '
                              "host's activity timeline with your name.",
                    ),
                  ),
                ],
              ],
            ),
            const SizedBox(height: 16),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                _ActionsMenu(host: host),
                SecondaryButton(
                  key: const Key('details-host-page'),
                  label: 'Open host page',
                  onPressed: () => Navigator.of(context).pop(HostPanelAction.hostPage),
                ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

/// One button that opens a small menu of what can be done to the host. Terminal, Connect and Remove
/// are behind it rather than three buttons side by side, so none is pressed by mistake; Remove sits
/// apart, in red.
class _ActionsMenu extends StatelessWidget {
  const _ActionsMenu({required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return PopupMenuButton<HostPanelAction>(
      key: const Key('details-actions'),
      tooltip: 'Actions',
      color: palette.surface,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      onSelected: (action) => Navigator.of(context).pop(action),
      itemBuilder: (context) => [
        const PopupMenuItem(value: HostPanelAction.terminal, child: Text('Open terminal')),
        const PopupMenuItem(value: HostPanelAction.connect, child: Text('Connect')),
        const PopupMenuDivider(),
        PopupMenuItem(
          value: HostPanelAction.remove,
          enabled: !host.removalRequested,
          child: Text('Remove host', style: TextStyle(color: palette.red)),
        ),
      ],
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        decoration: BoxDecoration(
          border: Border.all(color: palette.action),
          borderRadius: BorderRadius.circular(4),
          color: palette.accentWash(0.08),
        ),
        // An icon rather than a "▾" in the label: the display font has no such glyph, so it drew as an empty box in
        // a real browser (no widget test can see that, because tests draw every glyph as a box).
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('ACTIONS', style: AppTheme.label(color: palette.action, size: 10.5,)),
            const SizedBox(width: 4),
            Icon(Icons.arrow_drop_down, size: 18, color: palette.action),
          ],
        ),
      ),
    );
  }
}

class _Section extends StatelessWidget {
  const _Section({required this.title, required this.children, this.badge});

  final String title;
  final Widget? badge;
  final List<Widget> children;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Container(
      padding: const EdgeInsets.fromLTRB(14, 12, 14, 12),
      decoration: BoxDecoration(
        border: Border.all(color: palette.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(
            children: [
              Text(title.toUpperCase(), style: AppTheme.label(color: palette.muted, size: 9.5,)),
              if (badge != null) ...[const SizedBox(width: 10), badge!],
            ],
          ),
          const SizedBox(height: 6),
          ...children,
        ],
      ),
    );
  }
}

class _Fact extends StatelessWidget {
  const _Fact({required this.label, this.value, this.child, this.copy = false, this.copyKey, this.mono = false});

  final String label;
  final String? value;
  final Widget? child;
  final bool copy;
  final Key? copyKey;
  final bool mono;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final text = value;

    return Container(
      constraints: const BoxConstraints(minHeight: 34),
      decoration: BoxDecoration(border: Border(top: BorderSide(color: palette.border))),
      child: Row(
        children: [
          SizedBox(width: 108, child: Text(label, style: Theme.of(context).textTheme.bodySmall)),
          Expanded(
            child: child ??
                (text == null
                    ? const HintText('Not set yet')
                    : Text(text, style: mono ? AppTheme.mono(color: palette.text, size: 13) : null)),
          ),
          if (copy && text != null)
            IconButton(
              key: copyKey,
              tooltip: 'Copy ${label.toLowerCase()}',
              onPressed: () => copyHostValue(context, text),
              iconSize: 15,
              color: palette.muted,
              constraints: const BoxConstraints(minWidth: 28, minHeight: 28),
              padding: EdgeInsets.zero,
              visualDensity: VisualDensity.compact,
              icon: const Icon(Icons.copy_outlined),
            ),
        ],
      ),
    );
  }
}

/// What the administrator chose: how long (null is until they end it) and an optional reason.
class _MaintenanceChoice {
  const _MaintenanceChoice(this.hours, this.reason);

  final int? hours;
  final String? reason;
}

class _MaintenanceDialog extends StatefulWidget {
  const _MaintenanceDialog();

  @override
  State<_MaintenanceDialog> createState() => _MaintenanceDialogState();
}

class _MaintenanceDialogState extends State<_MaintenanceDialog> {
  static const _durations = <(String, int?)>[
    ('1 hour', 1),
    ('4 hours', 4),
    ('1 day', 24),
    ('7 days', 168),
    ('Until I end it', null),
  ];

  final _reason = TextEditingController();
  int? _hours = 24;

  @override
  void dispose() {
    _reason.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return AlertDialog(
      backgroundColor: palette.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(6), side: BorderSide(color: palette.border)),
      title: Text('Start maintenance mode', style: Theme.of(context).textTheme.titleLarge),
      content: SizedBox(
        width: 380,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const HintText('How long should it last? It ends by itself, so a device is not forgotten.'),
            const SizedBox(height: 10),
            Wrap(
              spacing: 8,
              runSpacing: 8,
              children: [
                for (final (label, hours) in _durations)
                  ChoiceChip(
                    key: Key('maintenance-duration-${hours ?? 'open'}'),
                    label: Text(label),
                    selected: _hours == hours,
                    onSelected: (_) => setState(() => _hours = hours),
                  ),
              ],
            ),
            const SizedBox(height: 14),
            TextField(
              key: const Key('maintenance-reason'),
              controller: _reason,
              maxLength: 500,
              decoration: const InputDecoration(labelText: 'Reason (optional)'),
            ),
          ],
        ),
      ),
      actions: [
        SecondaryButton(
          key: const Key('maintenance-cancel'),
          label: 'Cancel',
          onPressed: () => Navigator.of(context).pop(),
        ),
        PrimaryButton(
          key: const Key('maintenance-confirm'),
          label: 'Start',
          onPressed: () => Navigator.of(context).pop(
            _MaintenanceChoice(_hours, _reason.text.trim().isEmpty ? null : _reason.text.trim()),
          ),
        ),
      ],
    );
  }
}
