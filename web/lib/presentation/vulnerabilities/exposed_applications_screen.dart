import 'package:flutter/material.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/exposed_application.dart';
import '../../domain/usecases/vulnerability_usecases.dart';

/// Applications with an open CVE that Kintsugi's own patching will not fix: another tool owns them, or nobody does
/// and no update path was found.
///
/// Marking an application as managed elsewhere never takes it off this list. It only says whom to ask, because an
/// application nobody patches is an open door whoever is meant to close it. The order is the server's: most urgent
/// first, and an application with no owner and no route before one with a named owner.
class ExposedApplicationsScreen extends StatefulWidget {
  const ExposedApplicationsScreen({super.key});

  @override
  State<ExposedApplicationsScreen> createState() => _ExposedApplicationsScreenState();
}

class _ExposedApplicationsScreenState extends State<ExposedApplicationsScreen> {
  List<ExposedApplication>? _applications;
  String? _error;
  bool _busy = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final applications = await locator<GetExposedApplications>()();
      if (mounted) setState(() => _applications = applications);
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final applications = _applications;
    return PageScaffold(
      title: 'Exposed Applications',
      subtitle: 'Applications with an open CVE that Kintsugi will not patch itself. Someone else is meant to fix '
          'them, or nobody is. Marking an application as managed elsewhere does not hide its CVEs here.',
      children: [
        SectionHeader(
          title: 'Open Doors',
          hints: const [
            HintText(
              'Only applications with a confirmed CPE mapping are checked for CVEs, so an application missing '
              'here may simply not be mapped yet. See CVE Mapping.',
            ),
          ],
          actions: [SecondaryButton(label: 'Reload', onPressed: _busy ? null : _load)],
        ),
        if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 12)],
        if (applications == null)
          EmptyPanel(_error == null ? 'Loading…' : 'Could not load the list.')
        else if (applications.isEmpty)
          const EmptyPanel('No application with an open CVE is waiting on someone else to patch it.')
        else
          for (final application in applications) ...[
            _ExposedRow(application: application),
            const SizedBox(height: 10),
          ],
      ],
    );
  }
}

class _ExposedRow extends StatelessWidget {
  const _ExposedRow({required this.application});

  final ExposedApplication application;

  static String _criticalityLabel(ExposureCriticality value) => switch (value) {
        ExposureCriticality.critical => 'Critical',
        ExposureCriticality.high => 'High',
        ExposureCriticality.unscored => 'Unscored',
        ExposureCriticality.medium => 'Medium',
        ExposureCriticality.low => 'Low',
      };

  /// The palette's CVE keys, so these chips are the colours the CVE Reporting screen already uses.
  static String _criticalityKey(ExposureCriticality value) => switch (value) {
        ExposureCriticality.critical => 'cve-critical',
        ExposureCriticality.high => 'cve-high',
        ExposureCriticality.medium => 'cve-medium',
        ExposureCriticality.low => 'cve-low',
        ExposureCriticality.unscored => 'cve-unscored',
      };

  String get _evidence {
    final parts = <String>[
      if (application.worstCveId != null)
        '${application.worstCveId}${application.worstCvssScore != null ? ' (CVSS ${application.worstCvssScore!.toStringAsFixed(1)})' : ''}',
      if (application.cveCount > 1) '${application.cveCount} CVEs',
      '${application.hostCount} host(s)',
    ];
    return parts.join(' · ');
  }

  String get _nextStep => switch (application.route) {
        ExposureRoute.managedElsewhere =>
          'Ask ${application.owner} for a fixed version. It is theirs to patch, but it is still open here.',
        ExposureRoute.noUpgradePath =>
          'Nobody is named and no update path was found. Name an owner on Managed Elsewhere, or find a fix, '
              'or remove it.',
      };

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        child: Padding(
          padding: const EdgeInsets.all(16),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Wrap(
                spacing: 10,
                runSpacing: 6,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  Text(application.applicationName, style: Theme.of(context).textTheme.titleMedium),
                  StatusChip(
                    _criticalityLabel(application.criticality),
                    statusKey: _criticalityKey(application.criticality),
                  ),
                  if (application.knownExploited) const StatusChip('Exploited', statusKey: 'exploited'),
                  if (application.knownRansomwareUse) const StatusChip('Ransomware', statusKey: 'exploited'),
                  if (application.route == ExposureRoute.managedElsewhere)
                    StatusChip(
                      application.ownerIsAuto
                          ? 'Managed · ${application.owner} (auto)'
                          : 'Managed · ${application.owner}',
                      statusKey: 'managed-elsewhere',
                    )
                  else
                    const StatusChip('No owner · no update path', statusKey: 'check-failed'),
                ],
              ),
              const SizedBox(height: 6),
              HintText(_evidence),
              const SizedBox(height: 4),
              HintText(_nextStep),
            ],
          ),
        ),
      );
}
