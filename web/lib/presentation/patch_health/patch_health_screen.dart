import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/network/api_exception.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/usecases/host_usecases.dart';
import '../../domain/usecases/patch_failure_usecases.dart';
import 'patch_health_model.dart';

/// Which hosts are failing to patch, why, and what to do about it — on one screen, so nobody has to
/// open Failed Updates, then Hosts, then the host itself to piece the answer together.
///
/// Read-only: it explains and links, and acts on nothing. Patching a host from here is a separate
/// piece of work because it changes managed machines, which needs its own confirmation and its own
/// warning to the person at the keyboard.
class PatchHealthScreen extends StatefulWidget {
  const PatchHealthScreen({super.key});

  @override
  State<PatchHealthScreen> createState() => _PatchHealthScreenState();
}

class _PatchHealthScreenState extends State<PatchHealthScreen> {
  List<HealthItem>? _items;
  String? _error;
  bool _loading = false;
  HealthKind? _kind;
  String _search = '';
  String? _expandedKey;

  @override
  void initState() {
    super.initState();
    _load();
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });
    try {
      final failuresFuture = locator<GetPatchFailures>()();
      final hostsFuture = locator<GetHosts>()();
      final failures = await failuresFuture;
      final hosts = await hostsFuture;
      if (!mounted) return;
      setState(() => _items = buildHealthItems(failures: failures, hosts: hosts));
    } on ApiException catch (error) {
      if (mounted) setState(() => _error = error.message);
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  List<HealthItem> get _visible {
    final needle = _search.trim().toLowerCase();
    return [
      for (final item in _items ?? const <HealthItem>[])
        if ((_kind == null || item.kind == _kind) &&
            (needle.isEmpty ||
                item.hostname.toLowerCase().contains(needle) ||
                item.subject.toLowerCase().contains(needle) ||
                item.cause.toLowerCase().contains(needle) ||
                (item.details?.toLowerCase().contains(needle) ?? false)))
          item,
    ];
  }

  @override
  Widget build(BuildContext context) {
    final items = _items;

    return PageScaffold(
      title: 'Patch Health',
      children: [
        SectionHeader(
          title: 'Hosts That Are Failing To Patch',
          hints: const [
            HintText(
              'Every host with an update that failed or cannot be tried, with the reason in plain '
              'words and what to do about it. Click a row for the error and the details.',
            ),
            HintText(
              'It is built from the Failed Updates queue and the host list, so a row clears itself '
              'when that host next reports the update as installed. A host that is offline with '
              'updates waiting is listed too, since it is not failing, only unreachable.',
            ),
          ],
          actions: [SecondaryButton(label: 'Reload', onPressed: _loading ? null : _load)],
        ),
        if (_error != null) ...[AlertBox.error(_error!), const SizedBox(height: 12)],
        if (items == null)
          EmptyPanel(_loading ? 'Loading…' : 'Could not load Patch Health.')
        else ...[
          _Tiles(items: items, selected: _kind, onSelect: (kind) => setState(() {
                _kind = kind;
                _expandedKey = null;
              })),
          const SizedBox(height: 16),
          if (items.isEmpty)
            const EmptyPanel('No host is failing to patch, and none is offline with updates waiting.')
          else
            _Table(
              items: _visible,
              allItems: items,
              kind: _kind,
              search: _search,
              expandedKey: _expandedKey,
              onKind: (kind) => setState(() {
                _kind = kind;
                _expandedKey = null;
              }),
              onSearch: (value) => setState(() => _search = value),
              onToggle: (key) => setState(() => _expandedKey = _expandedKey == key ? null : key),
            ),
        ],
      ],
    );
  }
}

String _kindLabel(HealthKind? kind) => switch (kind) {
      null => 'All',
      HealthKind.osUpdate => 'Operating system',
      HealthKind.application => 'Applications',
      HealthKind.offline => 'Offline',
    };

class _Tiles extends StatelessWidget {
  const _Tiles({required this.items, required this.selected, required this.onSelect});

  final List<HealthItem> items;
  final HealthKind? selected;
  final void Function(HealthKind? kind) onSelect;

  int _count(HealthKind kind) => items.where((i) => i.kind == kind).length;

  @override
  Widget build(BuildContext context) {
    final tiles = <(String, int, HealthKind?)>[
      ('Hosts needing attention', hostsNeedingAttention(items), null),
      ('Operating system failures', _count(HealthKind.osUpdate), HealthKind.osUpdate),
      ('Application failures', _count(HealthKind.application), HealthKind.application),
      ('Offline with updates waiting', _count(HealthKind.offline), HealthKind.offline),
    ];

    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        for (final (label, count, kind) in tiles)
          SizedBox(
            width: 250,
            child: InkWell(
              onTap: () => onSelect(kind),
              child: KintsugiPanel(
                child: Container(
                  padding: const EdgeInsets.all(14),
                  decoration: selected == kind
                      ? BoxDecoration(border: Border.all(color: context.palette.neon))
                      : null,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '$count',
                        style: AppTheme.display(
                          color: count > 0 && kind != HealthKind.offline
                              ? context.palette.red
                              : context.palette.neon,
                          size: 28,
                        ),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        label.toUpperCase(),
                        style: AppTheme.display(color: context.palette.muted, size: 9.9, letterSpacing: 0.99),
                      ),
                    ],
                  ),
                ),
              ),
            ),
          ),
      ],
    );
  }
}

class _Table extends StatelessWidget {
  const _Table({
    required this.items,
    required this.allItems,
    required this.kind,
    required this.search,
    required this.expandedKey,
    required this.onKind,
    required this.onSearch,
    required this.onToggle,
  });

  final List<HealthItem> items;
  final List<HealthItem> allItems;
  final HealthKind? kind;
  final String search;
  final String? expandedKey;
  final void Function(HealthKind? kind) onKind;
  final void Function(String value) onSearch;
  final void Function(String key) onToggle;

  int _count(HealthKind? k) => k == null ? allItems.length : allItems.where((i) => i.kind == k).length;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            spacing: 8,
            runSpacing: 8,
            children: [
              for (final k in <HealthKind?>[null, ...HealthKind.values])
                _TabButton(
                  label: '${_kindLabel(k)} (${_count(k)})',
                  selected: kind == k,
                  onTap: () => onKind(k),
                ),
            ],
          ),
          const SizedBox(height: 12),
          KintsugiTable(
            minWidth: 1000,
            columns: [
              TableColumnSpec(
                label: 'Host',
                width: const FlexColumnWidth(1.3),
                filter: SearchField(value: search, hintText: 'Search host, cause or output…', onChanged: onSearch),
              ),
              const TableColumnSpec(label: 'Why it is failing', width: FlexColumnWidth(2)),
              const TableColumnSpec(label: 'Tries', width: FixedColumnWidth(70)),
              const TableColumnSpec(label: 'Since', width: FlexColumnWidth(1)),
              const TableColumnSpec(label: 'What to do', width: FlexColumnWidth(2)),
              const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(120)),
            ],
            rows: [
              for (final item in items)
                KintsugiTableRow(
                  cells: [
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(item.hostname),
                        if (item.operatingSystem case final os?) HintText(os),
                      ],
                    ),
                    // Stacked rather than side by side: the cause is a chip as wide as its sentence,
                    // and beside the name it squeezed "macOS update" into four lines.
                    Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Text(item.subject),
                        const SizedBox(height: 4),
                        StatusChip(
                          item.cause,
                          statusKey: item.kind == HealthKind.offline ? 'update-available' : 'patch-failed',
                        ),
                      ],
                    ),
                    item.tries == 0 ? const NoValue() : Text('${item.tries}'),
                    LocalTimestamp(item.since),
                    HintText(item.suggestedFix),
                    IconActionButton(
                      icon: expandedKey == item.key ? Icons.expand_less : Icons.expand_more,
                      tooltip: 'Show the details',
                      onPressed: () => onToggle(item.key),
                    ),
                  ],
                  expanded: expandedKey == item.key ? _Details(item: item) : null,
                ),
            ],
          ),
          if (items.isEmpty)
            const Padding(
              padding: EdgeInsets.only(top: 16),
              child: EmptyPanel('Nothing matches.'),
            ),
        ],
      );
}

class _TabButton extends StatelessWidget {
  const _TabButton({required this.label, required this.selected, required this.onTap});

  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return InkWell(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
        decoration: BoxDecoration(
          border: Border.all(color: selected ? palette.neon : Colors.transparent),
          borderRadius: BorderRadius.circular(3),
        ),
        child: Text(
          label.toUpperCase(),
          style: AppTheme.display(color: selected ? palette.neon : palette.muted, size: 10.5, letterSpacing: 1),
        ),
      ),
    );
  }
}

class _Details extends StatelessWidget {
  const _Details({required this.item});

  final HealthItem item;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final style = Theme.of(context).textTheme.bodyMedium;

    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(item.cause, style: style?.copyWith(fontWeight: FontWeight.w700, color: palette.neon)),
          const SizedBox(height: 4),
          Text(item.explanation, style: style),
          const SizedBox(height: 10),
          const SubHeadingTight('What to do'),
          const SizedBox(height: 4),
          Text(item.suggestedFix, style: style),
          if (item.details case final details?) ...[
            const SizedBox(height: 12),
            const SubHeadingTight('What the run reported'),
            const SizedBox(height: 6),
            Container(
              width: double.infinity,
              constraints: const BoxConstraints(maxHeight: 220),
              padding: const EdgeInsets.all(14),
              decoration: BoxDecoration(
                color: palette.accentWash(0.04),
                border: Border.all(color: palette.border),
                borderRadius: BorderRadius.circular(3),
              ),
              child: SingleChildScrollView(
                child: Text(details, style: AppTheme.mono(color: palette.text, size: 12.5)),
              ),
            ),
          ],
          const SizedBox(height: 12),
          if (item.kind != HealthKind.offline)
            SecondaryButton(
              label: 'Open in Failed Updates',
              onPressed: () => context.go(Routes.applicationsFailed),
            ),
        ],
      ),
    );
  }
}
