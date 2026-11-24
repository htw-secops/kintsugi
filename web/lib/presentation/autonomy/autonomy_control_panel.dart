import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/autonomy.dart';

/// One state of the fleet as the dial and the donut draw it.
class _Slice {
  const _Slice(this.key, this.label, this.count, this.color, this.meaning, this.causeKey, this.deviceDays);

  final String key;
  final String label;
  final int count;
  final Color color;
  final String meaning;

  /// The cause on the page this slice opens, or null for "nothing to patch".
  final String? causeKey;
  final double deviceDays;
}

/// The Autonomy control panel: a dial (how much of the fleet needs nothing), a donut (where the rest are, with
/// what each costs in device-days) and a check of whether unstable devices are why patching fails.
///
/// Built from `AutonomyFleet` and the causes the page already lists, so it cannot disagree with them. Colour is
/// never the only cue: every slice has its name and count beside it.
class AutonomyControlPanel extends StatelessWidget {
  const AutonomyControlPanel({super.key, required this.data, required this.onSelectCause});

  final Autonomy data;

  /// Called with the cause key a slice stands for, so the causes list below opens it.
  final ValueChanged<String> onSelectCause;

  double _days(String category) =>
      data.causes.where((c) => c.category == category).fold(0.0, (sum, c) => sum + c.deviceDays);

  /// The failure cause that costs the most, so "patch problem" opens the one worth reading first.
  String? get _worstFailureKey {
    final failures = data.causes.where((c) => c.category == 'Failure' && c.devices > 0).toList()
      ..sort((a, b) => b.deviceDays.compareTo(a.deviceDays));
    return failures.firstOrNull?.key;
  }

  @override
  Widget build(BuildContext context) {
    final fleet = data.fleet!;
    final palette = context.palette;
    final slices = [
      _Slice('nothing', 'Nothing to patch', fleet.nothingToPatch, palette.green, 'Up to date, or off with nothing waiting.', null, 0),
      _Slice('disconnected', 'Not connected', fleet.disconnected, palette.amber, 'Has an update waiting but cannot be reached.', 'disconnected', _days('Disconnected')),
      _Slice('restart', 'Restart pending', fleet.restart, palette.action, 'Updated, waiting for a restart.', 'restart', _days('Restart')),
      _Slice('problem', 'Patch problem', fleet.problem, palette.red, 'A patch failed and needs a fix.', _worstFailureKey, _days('Failure')),
    ];
    final total = fleet.total;
    final percent = total == 0 ? 0 : (fleet.nothingToPatch * 100 / total).round();

    return Wrap(
      spacing: 12,
      runSpacing: 12,
      crossAxisAlignment: WrapCrossAlignment.start,
      children: [
        _card(
          context,
          width: 250,
          title: 'PATCH HEALTH',
          child: _Ring(
            key: const Key('control-dial'),
            slices: [slices.first],
            total: total,
            center: '$percent%',
            sub: '${fleet.nothingToPatch} of $total devices',
            note: 'need nothing',
            thickness: 14,
          ),
        ),
        _card(
          context,
          width: 380,
          title: 'WHERE THE REST ARE',
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              _Ring(
                key: const Key('control-donut'),
                slices: slices,
                total: total,
                center: '$total',
                sub: 'devices',
                thickness: 26,
              ),
              const SizedBox(height: 8),
              for (final s in slices) _LegendRow(slice: s, onTap: s.causeKey == null || s.count == 0 ? null : () => onSelectCause(s.causeKey!)),
              const SizedBox(height: 4),
              const HintText('Each device is in one state, worst first. Device-days is the cost: days waiting, added up. Click a row to open its cause below.'),
            ],
          ),
        ),
        _card(context, width: 380, title: 'UNSTABLE DEVICES: IS THIS WHY PATCHING FAILS?', child: _UnstableCard(fleet: fleet)),
      ],
    );
  }

  Widget _card(BuildContext context, {required double width, required String title, required Widget child}) => SizedBox(
        width: width,
        child: KintsugiPanel(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(title, style: AppTheme.label(color: context.palette.muted, size: 9.9)),
                const SizedBox(height: 10),
                child,
              ],
            ),
          ),
        ),
      );
}

class _LegendRow extends StatelessWidget {
  const _LegendRow({required this.slice, required this.onTap});

  final _Slice slice;
  final VoidCallback? onTap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final cost = slice.deviceDays > 0 ? ' · ${slice.deviceDays.round()} device-days' : '';

    return Tooltip(
      message: slice.meaning,
      child: InkWell(
        key: Key('control-row-${slice.key}'),
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.symmetric(vertical: 4),
          child: Row(
            children: [
              Container(width: 10, height: 10, color: slice.color),
              const SizedBox(width: 8),
              Expanded(child: Text(slice.label, style: Theme.of(context).textTheme.bodyMedium)),
              Text('${slice.count}$cost', style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted)),
            ],
          ),
        ),
      ),
    );
  }
}

class _UnstableCard extends StatelessWidget {
  const _UnstableCard({required this.fleet});

  final AutonomyFleet fleet;

  @override
  Widget build(BuildContext context) {
    if (fleet.unstable == 0) {
      return const HintText('No unstable devices in the last 30 days.');
    }

    final facts = [
      ('${fleet.unstableNearPatch}', 'shut down unexpectedly within 2 hours of a patch'),
      ('${fleet.unstableOnProblem} of ${fleet.problem}', 'patch problems are on unstable devices'),
      ('${fleet.unstable}', 'unstable devices in all'),
    ];

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (final (value, text) in facts)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: Row(
              crossAxisAlignment: CrossAxisAlignment.baseline,
              textBaseline: TextBaseline.alphabetic,
              children: [
                SizedBox(width: 78, child: Text(value, style: AppTheme.label(color: context.palette.text, size: 20))),
                Expanded(child: Text(text, style: Theme.of(context).textTheme.bodySmall)),
              ],
            ),
          ),
        const HintText(
          'Unstable means 3 or more unexpected shutdowns in 7 days, or a fatal hardware error. The agents report when '
          'a patch ends, not when it starts, so "within 2 hours" is a strong clue, not proof. A lost connection in the '
          'middle of a patch is not recorded.',
        ),
      ],
    );
  }
}

/// A ring cut into [slices]. With one slice it is a dial: that slice's share of [total] on a grey track.
class _Ring extends StatelessWidget {
  const _Ring({
    super.key,
    required this.slices,
    required this.total,
    required this.center,
    required this.sub,
    required this.thickness,
    this.note,
  });

  final List<_Slice> slices;
  final int total;
  final String center;
  final String sub;
  final String? note;
  final double thickness;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Semantics(
      label: '$center, $sub${note == null ? '' : ' $note'}',
      child: SizedBox(
        width: 200,
        height: 200,
        child: Stack(
          alignment: Alignment.center,
          children: [
            CustomPaint(
              size: const Size(200, 200),
              painter: _RingPainter(
                values: [for (final s in slices) (s.count.toDouble(), s.color)],
                total: total.toDouble(),
                track: palette.border,
                thickness: thickness,
              ),
            ),
            Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                Text(center, style: AppTheme.label(color: palette.text, size: 30)),
                Text(sub, style: Theme.of(context).textTheme.bodySmall),
                if (note != null) Text(note!, style: Theme.of(context).textTheme.bodySmall?.copyWith(color: palette.muted)),
              ],
            ),
          ],
        ),
      ),
    );
  }
}

class _RingPainter extends CustomPainter {
  _RingPainter({required this.values, required this.total, required this.track, required this.thickness});

  final List<(double, Color)> values;
  final double total;
  final Color track;
  final double thickness;

  @override
  void paint(Canvas canvas, Size size) {
    final rect = Rect.fromCircle(center: size.center(Offset.zero), radius: size.width / 2 - thickness / 2);
    final paint = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = thickness;

    canvas.drawArc(rect, 0, math.pi * 2, false, paint..color = track);
    if (total <= 0) return;

    // A small gap between slices, so neighbours read apart without a stroke around each.
    const gap = 0.03;
    var start = -math.pi / 2;
    for (final (value, color) in values) {
      if (value <= 0) continue;
      final sweep = value / total * math.pi * 2;
      canvas.drawArc(rect, start + gap / 2, math.max(0.0, sweep - (values.length > 1 ? gap : 0)), false, paint..color = color);
      start += sweep;
    }
  }

  @override
  bool shouldRepaint(_RingPainter old) =>
      old.values != values || old.total != total || old.track != track || old.thickness != thickness;
}
