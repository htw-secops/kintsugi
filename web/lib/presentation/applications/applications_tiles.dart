import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/panel.dart';
import 'applications_bloc.dart';

/// The four statuses that ask something of an administrator, as tiles above the table: how many
/// rows are in each, and one click to list them. Same shape as `HostsTiles`.
///
/// "Up To Date" is left out on purpose: it needs nothing, and a fifth tile would dilute the four
/// that do. Pressing the tile that is already chosen clears the status filter.
class ApplicationsTiles extends StatelessWidget {
  const ApplicationsTiles({super.key, required this.state, required this.onStatus});

  final ApplicationsState state;
  final ValueChanged<String> onStatus;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final tiles = <(String, String, Color, String)>[
      ('update-available', 'Update Available', palette.amber, 'patch these now'),
      ('review-sign', 'Review And Sign', palette.action, 'a person must sign the script'),
      ('check-failed', 'Check Failed', palette.red, 'open the row to fix it'),
      ('not-found', 'No Known Path', palette.muted, 'no update method found yet'),
    ];

    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        for (final (key, label, color, hint) in tiles)
          _Tile(
            statusKey: key,
            label: label,
            hint: hint,
            color: color,
            count: state.statusCount(key),
            selected: state.filters.statusKey == key,
            onTap: () => onStatus(key),
          ),
      ],
    );
  }
}

class _Tile extends StatelessWidget {
  const _Tile({
    required this.statusKey,
    required this.label,
    required this.hint,
    required this.color,
    required this.count,
    required this.selected,
    required this.onTap,
  });

  final String statusKey;
  final String label;
  final String hint;
  final Color color;
  final int count;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Semantics(
      button: true,
      selected: selected,
      label: '$label, $count',
      child: MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          key: Key('status-tile-$statusKey'),
          onTap: onTap,
          child: Container(
            width: 210,
            padding: const EdgeInsets.all(1.5),
            decoration: BoxDecoration(
              border: Border.all(color: selected ? color : Colors.transparent, width: 1.5),
              borderRadius: BorderRadius.circular(8),
            ),
            child: KintsugiPanel(
              padding: const EdgeInsets.fromLTRB(16, 14, 16, 12),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text('$count', style: AppTheme.label(color: color, size: 22,)),
                  const SizedBox(height: 6),
                  Text(label.toUpperCase(),
                      style: AppTheme.label(color: palette.muted, size: 9,)),
                  const SizedBox(height: 3),
                  Text(hint, style: Theme.of(context).textTheme.bodySmall?.copyWith(fontSize: 11.5)),
                ],
              ),
            ),
          ),
        ),
      ),
    );
  }
}
