import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';

/// A map of the product: how a patch gets from "an agent saw an old version" to "an administrator can
/// prove it was applied", and where each part of that lives.
///
/// Static on purpose. It reads no server data, so it needs no route and no BLoC, and it cannot
/// disagree with the screens it points at. Every card is a link to the screen that does the work,
/// which is the only reason the page exists: a new administrator can see the sidebar's seven menus
/// and still not know which one to start in.
class OverviewScreen extends StatelessWidget {
  const OverviewScreen({super.key});

  /// The flow, in order. Each step names the screen where it happens.
  static const steps = <OverviewItem>[
    OverviewItem('Detect', 'Agents report the applications installed on each host.', 'Hosts', Routes.hosts),
    OverviewItem('Research', 'An update script is found for each application.', 'Applications', Routes.applications),
    OverviewItem('Review and sign', 'A person approves every script before any agent runs it.', 'Upgrade Scripts', Routes.upgradeScripts),
    OverviewItem('Deploy', 'Agents patch inside the time window you set.', 'Patching Policy', Routes.settingsPatchingPolicy),
    OverviewItem('Audit', 'Every sign-in, signature and patch is recorded.', 'Auditing', Routes.settingsAuditing),
  ];

  /// What an administrator comes here to do. Only things the product does today.
  static const capabilities = <OverviewItem>[
    OverviewItem('Vulnerability management', 'CVEs matched to your fleet, with the exploited ones first.', 'CVE Reporting', Routes.vulnerabilities),
    OverviewItem('Complete visibility', 'Every host, with its health and what is waiting to be patched.', 'Hosts', Routes.hosts),
    OverviewItem('Flexible patch scheduling', 'Choose the interval and the window, or press Patch now in an emergency.', 'Patching Policy', Routes.settingsPatchingPolicy),
    OverviewItem('Full auditability', 'Keep a record of who did what, and when.', 'Auditing', Routes.settingsAuditing),
    OverviewItem('Compliance support', 'Send your hosts and vulnerabilities to Vanta as evidence.', 'Vanta', Routes.settingsVanta),
  ];

  @override
  Widget build(BuildContext context) => const PageScaffold(
        title: 'Overview',
        subtitle: 'How patching works in Kintsugi, and where to do each part.',
        children: [
          _Heading('The flow'),
          _Cards(items: steps, numbered: true),
          SizedBox(height: 32),
          _Heading('What you can do'),
          _Cards(items: capabilities),
        ],
      );
}

/// One card on the page: a title, a sentence, and the screen it opens.
class OverviewItem {
  const OverviewItem(this.title, this.text, this.linkLabel, this.path);

  final String title;
  final String text;
  final String linkLabel;
  final String path;
}

class _Heading extends StatelessWidget {
  const _Heading(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Text(text.toUpperCase(), style: Theme.of(context).textTheme.titleMedium),
      );
}

class _Cards extends StatelessWidget {
  const _Cards({required this.items, this.numbered = false});

  final List<OverviewItem> items;

  /// Only the flow is a sequence. Numbering the capabilities would claim an order they do not have.
  final bool numbered;

  @override
  Widget build(BuildContext context) => Wrap(
        spacing: 12,
        runSpacing: 12,
        children: [
          for (final (index, item) in items.indexed)
            _Card(item: item, step: numbered ? index + 1 : null),
        ],
      );
}

class _Card extends StatelessWidget {
  const _Card({required this.item, this.step});

  final OverviewItem item;
  final int? step;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Semantics(
      button: true,
      label: '${item.title}. Open ${item.linkLabel}.',
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          key: Key('overview-${item.title}'),
          onTap: () => context.go(item.path),
          child: SizedBox(
            width: 230,
            child: KintsugiPanel(
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  if (step != null) ...[
                    Text('$step', style: AppTheme.display(color: palette.neon, size: 18, letterSpacing: 0.5)),
                    const SizedBox(height: 6),
                  ],
                  Text(item.title.toUpperCase(),
                      style: AppTheme.display(color: palette.neonSoft, size: 11.5, letterSpacing: 1.1)),
                  const SizedBox(height: 6),
                  Text(item.text, style: Theme.of(context).textTheme.bodySmall),
                  const SizedBox(height: 10),
                  Text('Open ${item.linkLabel}  →',
                      style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.neon)),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
