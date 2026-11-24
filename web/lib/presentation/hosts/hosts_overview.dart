import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/host.dart';
import 'hosts_bloc.dart';
import 'hosts_filters.dart';
import 'hosts_format.dart';

/// The five numbers across the top: how many hosts are enrolled and how many are in each of the three
/// states, and how many still have no department or location.
///
/// Counted over the hosts the filters leave, so narrowing the table narrows the tiles with it.
class HostsTiles extends StatelessWidget {
  const HostsTiles({super.key, required this.state});

  final HostsState state;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final shown = state.visibleHosts.length;

    final tiles = <(String, int, Color, String)>[
      ('Enrolled in Kintsugi', shown, palette.action, state.isNarrowed ? 'of ${state.hosts.length}, after the filters' : 'hosts'),
      ('Healthy', state.countIn(HostHealthState.healthy), palette.green, 'nothing waiting'),
      ('Pending', state.countIn(HostHealthState.pending), palette.amber, 'agent behind, or not seen for days'),
      ('Problem', state.countIn(HostHealthState.problem), palette.red, 'a patch failed, needs a fix'),
      ('No department or location', state.withoutPlace, palette.muted, 'set by import or in bulk'),
    ];

    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        for (final (label, count, color, hint) in tiles)
          SizedBox(
            width: 210,
            child: KintsugiPanel(
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    '$count',
                    key: Key('tile-$label'),
                    style: AppTheme.label(color: color, size: 22,),
                  ),
                  const SizedBox(height: 6),
                  Text(label.toUpperCase(), style: AppTheme.label(color: palette.muted, size: 9,)),
                  const SizedBox(height: 3),
                  Text(hint, style: Theme.of(context).textTheme.bodySmall?.copyWith(fontSize: 11.5)),
                ],
              ),
            ),
          ),
      ],
    );
  }
}

/// "Where it fails": one thin bar per department, location, system or model, split green, amber and
/// red by how many of its hosts are healthy, pending or have a problem.
///
/// **No numbers on the bars.** The share is the picture; the count is a hover away
/// ([HostGroup.tooltip]). Two kinds of click, kept apart on purpose:
///  * the group's **name** filters by the group alone ([onGroup]);
///  * a **coloured part** applies the group *and* that health state at once ([onSegment]) and the
///    screen then brings the matching hosts into view, so "the amber part of the HR bar" is one click
///    from the list of those hosts.
class WhereItFails extends StatelessWidget {
  const WhereItFails({
    super.key,
    required this.state,
    required this.onGroupBy,
    required this.onGroup,
    required this.onSegment,
  });

  final HostsState state;
  final ValueChanged<HostGroupBy> onGroupBy;
  final void Function(String group) onGroup;
  final void Function(String group, HostHealthState health) onSegment;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final groups = state.groups;
    final field = state.groupBy.field;

    return KintsugiPanel(
      padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Wrap(
            spacing: 12,
            runSpacing: 8,
            crossAxisAlignment: WrapCrossAlignment.center,
            children: [
              Text('WHERE IT FAILS', style: AppTheme.label(color: palette.text, size: 12.5,)),
              Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  for (final option in HostGroupBy.values)
                    _GroupByButton(
                      option: option,
                      selected: state.groupBy == option,
                      onTap: () => onGroupBy(option),
                    ),
                ],
              ),
            ],
          ),
          const SizedBox(height: 10),
          Wrap(
            spacing: 20,
            runSpacing: 6,
            children: [
              for (final health in HostHealthState.values) _LegendDot(health: health),
            ],
          ),
          const SizedBox(height: 6),
          const HintText('Hover a colour to see how many hosts. Click a colour to list those hosts below. '
              'Click a name to filter by it.'),
          const SizedBox(height: 12),
          if (groups.isEmpty)
            const HintText('No hosts to group.')
          else
            for (final group in groups)
              _GroupRow(
                group: group,
                selected: state.filters.valuesOf(field).contains(group.key),
                activeSegment: [
                  for (final health in HostHealthState.values)
                    if (state.filters.segmentActive(field, group.key, health)) health,
                ].firstOrNull,
                onName: () => onGroup(group.key),
                onSegment: (health) => onSegment(group.key, health),
              ),
        ],
      ),
    );
  }
}

class _GroupByButton extends StatelessWidget {
  const _GroupByButton({required this.option, required this.selected, required this.onTap});

  final HostGroupBy option;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return InkWell(
      key: Key('group-by-${option.name}'),
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 7),
        decoration: BoxDecoration(
          border: Border.all(color: selected ? palette.action : palette.border),
          color: selected ? palette.accentWash(0.08) : null,
        ),
        child: Text(
          'BY ${option.label.toUpperCase()}',
          style: AppTheme.label(color: selected ? palette.action : palette.muted, size: 9.5,),
        ),
      ),
    );
  }
}

class _LegendDot extends StatelessWidget {
  const _LegendDot({required this.health});

  final HostHealthState health;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final meaning = switch (health) {
      HostHealthState.healthy => 'Healthy',
      HostHealthState.pending => 'Pending: agent behind, or not seen for days',
      HostHealthState.problem => 'Problem: a patch failed, needs a fix',
    };

    return Row(
      mainAxisSize: MainAxisSize.min,
      children: [
        Container(
          width: 9,
          height: 9,
          decoration: BoxDecoration(color: hostHealthColor(palette, health), shape: BoxShape.circle),
        ),
        const SizedBox(width: 7),
        Text(meaning, style: Theme.of(context).textTheme.bodySmall),
      ],
    );
  }
}

class _GroupRow extends StatelessWidget {
  const _GroupRow({
    required this.group,
    required this.selected,
    required this.activeSegment,
    required this.onName,
    required this.onSegment,
  });

  final HostGroup group;
  final bool selected;

  /// The state already applied together with this group, so its segment can be drawn as chosen.
  final HostHealthState? activeSegment;
  final VoidCallback onName;
  final ValueChanged<HostHealthState> onSegment;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Container(
      margin: const EdgeInsets.symmetric(vertical: 1),
      padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 7),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(4),
        color: selected ? palette.accentWash(0.07) : null,
        border: Border.all(color: selected ? palette.action : Colors.transparent),
      ),
      child: Row(
        children: [
          SizedBox(
            width: 150,
            child: MouseRegion(
              cursor: SystemMouseCursors.click,
              child: GestureDetector(
                key: Key('group-name-${group.key}'),
                behavior: HitTestBehavior.opaque,
                onTap: onName,
                child: Text(group.key, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w500)),
              ),
            ),
          ),
          const SizedBox(width: 20),
          Expanded(
            child: ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: SizedBox(
                height: 8,
                child: DecoratedBox(
                  decoration: BoxDecoration(color: palette.muted.withValues(alpha: 0.12)),
                  child: Row(
                    children: [
                      for (final health in HostHealthState.values)
                        if (group.countOf(health) > 0)
                          Expanded(
                            flex: group.countOf(health),
                            child: Tooltip(
                              message: group.tooltip(health),
                              waitDuration: const Duration(milliseconds: 150),
                              child: MouseRegion(
                                cursor: SystemMouseCursors.click,
                                child: GestureDetector(
                                  key: Key('segment-${group.key}-${health.name}'),
                                  behavior: HitTestBehavior.opaque,
                                  onTap: () => onSegment(health),
                                  child: Container(
                                    margin: const EdgeInsets.only(right: 2),
                                    decoration: BoxDecoration(
                                      color: hostHealthColor(palette, health),
                                      border: activeSegment == health
                                          ? Border.all(color: palette.text, width: 1.5)
                                          : null,
                                    ),
                                  ),
                                ),
                              ),
                            ),
                          ),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ],
      ),
    );
  }
}
