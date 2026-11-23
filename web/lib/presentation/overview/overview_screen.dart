import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';

/// A map of the product: how a patch gets from "an agent saw an old version" to "an administrator can
/// prove it was applied", and where each part of that lives.
///
/// Static on purpose. It reads no server data, so it needs no route and no BLoC, and it cannot
/// disagree with the screens it points at. Each card says one thing; its arrow opens a paragraph
/// and a button to the screen that does the work. **Every sentence here is a claim about the
/// product**, so check it against the screen it links to before changing it — the Audit card says
/// plainly that nothing is sent yet, because `src/CLAUDE.md` ("Audit event shipping") says so.
class OverviewScreen extends StatelessWidget {
  const OverviewScreen({super.key});

  /// The flow, in order. Each step names the screen where it happens.
  static const steps = <OverviewItem>[
    OverviewItem(
      'Detect',
      'Agents report the applications installed on each host.',
      'Each agent checks in on a schedule and reports its operating system and applications. '
          'A new host appears in Hosts after its first check-in.',
      'Hosts',
      Routes.hosts,
    ),
    OverviewItem(
      'Research',
      'An update script is found for each application.',
      'Package managers such as Homebrew and winget get a fixed script. Other applications use '
          'the AI agent you configured. "Check for Updates" then looks for newer versions.',
      'Applications',
      Routes.applications,
    ),
    OverviewItem(
      'Review and sign',
      'A person approves every script before an agent runs it.',
      'A script written by AI or edited by hand is unsigned, and no agent will run it. Read it, '
          'then sign it from the application row or on Upgrade Scripts.',
      'Upgrade Scripts',
      Routes.upgradeScripts,
    ),
    OverviewItem(
      'Deploy',
      'Agents patch on the interval you set.',
      'Set how often agents patch and how long a restart can be put off. In an emergency, press '
          'Patch now on an application row.',
      'Patching Policy',
      Routes.settingsPatchingPolicy,
    ),
    OverviewItem(
      'Audit',
      'Choose where audit records will be sent.',
      'Pick the logging service in Settings. Kintsugi does not send events yet, so this only '
          'stores the destination for when it does.',
      'Auditing',
      Routes.settingsAuditing,
    ),
  ];

  /// What an administrator comes here to do. Only things the product does today.
  static const capabilities = <OverviewItem>[
    OverviewItem(
      'Vulnerabilities',
      'CVEs matched to your fleet, exploited ones first.',
      'CVE Reporting starts with the vulnerabilities attackers are using now. CVE Mapping lists '
          'the applications Kintsugi could not match yet, so you know what it cannot see.',
      'CVE Reporting',
      Routes.vulnerabilities,
    ),
    OverviewItem(
      'Complete visibility',
      'Every host, its health, and what is waiting to be patched.',
      'Filter hosts by health, department or location. Open one host to see its activity and '
          'start a remote session.',
      'Hosts',
      Routes.hosts,
    ),
    OverviewItem(
      'Flexible patch scheduling',
      'Choose how often agents patch.',
      'Set the interval and how long a required restart can be deferred. Use Patch now on an '
          'application row when it cannot wait.',
      'Patching Policy',
      Routes.settingsPatchingPolicy,
    ),
    OverviewItem(
      'Audit destination',
      'Set the logging service for audit records.',
      'Datadog, Loki, Splunk and others are supported as destinations. Sending events is not '
          'switched on yet.',
      'Auditing',
      Routes.settingsAuditing,
    ),
    OverviewItem(
      'Compliance support',
      'Send patch state to Vanta as evidence.',
      'Run a sync from Settings. Vanta then shows your hosts and vulnerabilities as compliance '
          'evidence.',
      'Vanta',
      Routes.settingsVanta,
    ),
  ];

  @override
  Widget build(BuildContext context) => const PageScaffold(
    title: 'Overview',
    subtitle: 'How patching works in Kintsugi, and where to do each part. Press the arrow on a card for more.',
    children: [
      _Heading('The flow'),
      _Grid(items: steps, numbered: true),
      SizedBox(height: 36),
      _Heading('What you can do'),
      _Grid(items: capabilities),
    ],
  );
}

/// One card on the page: a title, a sentence, a longer paragraph, and the screen it opens.
class OverviewItem {
  const OverviewItem(
    this.title,
    this.text,
    this.detail,
    this.linkLabel,
    this.path,
  );

  final String title;
  final String text;
  final String detail;
  final String linkLabel;
  final String path;
}

class _Heading extends StatelessWidget {
  const _Heading(this.text);

  final String text;

  @override
  Widget build(BuildContext context) => Padding(
    padding: const EdgeInsets.only(bottom: 14),
    // The guide's section heading: bold, sentence case.
    child: Text(text, style: Theme.of(context).textTheme.titleLarge),
  );
}

/// Cards of one width, in rows. The column count follows the width the page has, so five steps sit
/// in one row on a wide window and fold into 3 + 2 or 2 + 2 + 1 on a narrow one.
///
/// Equal height at rest comes from [_Card.minHeight], not from measuring the row: an
/// `IntrinsicHeight` row measured a wrapped title at the wrong width and clipped a card's last
/// line. A card that is opened grows on its own; the ones beside it keep their height.
class _Grid extends StatelessWidget {
  const _Grid({required this.items, this.numbered = false});

  final List<OverviewItem> items;

  /// Only the flow is a sequence. Numbering the capabilities would claim an order they do not have.
  final bool numbered;

  static const _gap = 14.0;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, constraints) {
      final width = constraints.maxWidth;
      final columns = width >= 1100
          ? 5
          : (width >= 800 ? 3 : (width >= 520 ? 2 : 1));
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (var start = 0; start < items.length; start += columns) ...[
            if (start > 0) const SizedBox(height: _gap),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                for (var i = start; i < start + columns; i++) ...[
                  if (i > start) const SizedBox(width: _gap),
                  Expanded(
                    child: i < items.length
                        ? _Card(item: items[i], step: numbered ? i + 1 : null)
                        : const SizedBox.shrink(),
                  ),
                ],
              ],
            ),
          ],
        ],
      );
    },
  );
}

class _Card extends StatefulWidget {
  const _Card({required this.item, this.step});

  final OverviewItem item;
  final int? step;

  /// The height every closed card has. Sized for the longest title and sentence at the narrowest
  /// five-column width, so no card needs more and none is clipped.
  static const minHeight = 232.0;

  @override
  State<_Card> createState() => _CardState();
}

class _CardState extends State<_Card> {
  bool _open = false;
  bool _hover = false;

  void _toggle() => setState(() => _open = !_open);

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final item = widget.item;
    final theme = Theme.of(context).textTheme;
    final highlighted = _open || _hover;

    return Semantics(
      button: true,
      expanded: _open,
      label: '${item.title}. ${item.text}',
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        onEnter: (_) => setState(() => _hover = true),
        onExit: (_) => setState(() => _hover = false),
        child: GestureDetector(
          key: Key('overview-${item.title}'),
          behavior: HitTestBehavior.opaque,
          onTap: _toggle,
          child: Container(
            padding: const EdgeInsets.all(1.5),
            decoration: BoxDecoration(
              border: Border.all(
                color: highlighted ? palette.action : Colors.transparent,
                width: 1.5,
              ),
              borderRadius: BorderRadius.circular(12),
            ),
            child: KintsugiPanel(
              padding: const EdgeInsets.fromLTRB(18, 16, 18, 12),
              child: ConstrainedBox(
                constraints: const BoxConstraints(
                  minHeight: _Card.minHeight - 28 - 3,
                ),
                // The arrow is pinned to the bottom right of a Stack, so it is in the same place on
                // every card whatever the text above it takes.
                child: Stack(
                  children: [
                    SizedBox(
                      width: double.infinity,
                      child: Padding(
                        padding: const EdgeInsets.only(bottom: 40),
                        child: Column(
                          crossAxisAlignment: CrossAxisAlignment.start,
                          children: [
                            if (widget.step != null) ...[
                              Text(
                                '${widget.step}',
                                style: AppTheme.label(color: palette.action, size: 24),
                              ),
                              const SizedBox(height: 6),
                            ],
                            Text(
                              item.title,
                              style: theme.titleMedium,
                            ),
                            const SizedBox(height: 10),
                            Text(
                              item.text,
                              style: theme.bodyMedium?.copyWith(
                                color: palette.text,
                                fontSize: 14.5,
                                height: 1.45,
                              ),
                            ),
                            if (_open) ...[
                              const SizedBox(height: 14),
                              Text(
                                item.detail,
                                style: theme.bodyMedium?.copyWith(
                                  color: palette.text.withValues(alpha: 0.78),
                                  fontSize: 13.5,
                                  height: 1.5,
                                ),
                              ),
                              const SizedBox(height: 14),
                              SecondaryButton(
                                key: Key('overview-open-${item.title}'),
                                label: 'Open ${item.linkLabel}',
                                onPressed: () => context.go(item.path),
                              ),
                            ],
                          ],
                        ),
                      ),
                    ),
                    Positioned(
                      right: 0,
                      bottom: 0,
                      child: Row(
                        mainAxisSize: MainAxisSize.min,
                        children: [
                          Text(
                            _open ? 'LESS' : 'MORE',
                            style: AppTheme.label(color: palette.action),
                          ),
                          const SizedBox(width: 4),
                          AnimatedRotation(
                            turns: _open ? 0.5 : 0,
                            duration: const Duration(milliseconds: 180),
                            child: Icon(
                              Icons.expand_more,
                              color: palette.action,
                              size: 28,
                            ),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}
