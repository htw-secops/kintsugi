import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/autonomy.dart';
import '../../domain/entities/vulnerability.dart';
import 'autonomy_cve.dart';
import 'autonomy_model.dart';

/// Rings under the key figures, for a fast look: devices up to date or failing, device health, what the delay
/// costs, and (when [findings] is given) which CVEs reach the most applications, with a year of CVEs by day under them. Each is a number in the middle with slices around it, and the words are in the
/// legend beside it, so a colour is never the only signal.
///
/// They sit on a navy panel in both themes (the sidebar's navy) and use the dark palette's colours, which
/// are the ones made for a dark ground; the light palette's green and amber are dark text colours and would
/// vanish on it. Every ring is drawn at the same size, whatever its slices, so the four line up.
class AutonomyRings extends StatelessWidget {
  const AutonomyRings({super.key, required this.data, this.findings, this.total, this.now});

  final Autonomy data;

  /// The fleet's CVEs. Null while they load or when they could not be read: the CVE ring and calendar are then
  /// left out and the rest of the page is unaffected.
  final List<VulnerabilityFinding>? findings;

  /// How many CVEs the fleet has in all, when the server holds more than [findings].
  final int? total;

  /// For the calendar's last day; the clock when null. Passed in by tests.
  final DateTime? now;

  /// Four cards and the gaps between them: how wide the calendar is allowed to be.
  static const _cardWidth = 340.0, _gap = 12.0;

  @override
  Widget build(BuildContext context) {
    final f = findings;
    final rings = [...ringsFor(data), if (f != null) cveRing(f, total: total)];
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Wrap(
        spacing: _gap,
        runSpacing: _gap,
        children: [
          for (final ring in rings) SizedBox(width: _cardWidth, child: _RingCard(ring: ring)),
        ],
      ),
      if (f != null) ...[
        const SizedBox(height: _gap),
        ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 4 * _cardWidth + 3 * _gap),
          child: CveCalendar(findings: f, now: now ?? DateTime.now(), total: total),
        ),
      ],
    ]);
  }
}

/// A status slice uses the dark palette's good, waiting and bad; a cost slice uses the chart series, the same in both
/// themes, so no cost is drawn in a colour that reads as "good".
Color _colourOf(RingSlice slice, KintsugiPalette palette) {
  final series = slice.series;
  if (series != null) return palette.chart[series % palette.chart.length];
  const dark = KintsugiPalette.dark;
  return switch (slice.kind) {
    RingKind.good => dark.green,
    RingKind.waiting => dark.amber,
    RingKind.bad => dark.red,
    RingKind.other => dark.muted,
  };
}

class _RingCard extends StatelessWidget {
  const _RingCard({required this.ring});

  final RingData ring;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final total = ring.total;
    final slices = [
      for (final s in ring.slices) if (s.value > 0) s,
    ];

    return Container(
      key: Key('ring-${ring.title}'),
      padding: const EdgeInsets.all(18),
      decoration: BoxDecoration(
        color: palette.nav,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: palette.navLine.withValues(alpha: 0.5)),
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text(ring.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: palette.onNav, fontSize: 16, fontWeight: FontWeight.w700)),
        Text(ring.subtitle, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: palette.navMuted, fontSize: 12.5)),
        const SizedBox(height: 8),
        Center(
          child: SizedBox(
            width: 280,
            height: 280,
            child: TweenAnimationBuilder<double>(
              tween: Tween(begin: 0, end: 1),
              duration: const Duration(milliseconds: 900),
              curve: Curves.easeOutCubic,
              builder: (context, t, _) => Stack(alignment: Alignment.center, children: [
                CustomPaint(
                  size: const Size(280, 280),
                  painter: _RingPainter(slices: slices, progress: t, palette: palette, track: palette.navLine.withValues(alpha: 0.35)),
                ),
                Column(mainAxisSize: MainAxisSize.min, children: [
                  Text(ring.centre, style: TextStyle(color: palette.onNav, fontSize: 40, fontWeight: FontWeight.w700)),
                  Text(ring.note, style: TextStyle(color: palette.navMuted, fontSize: 12.5)),
                ]),
              ]),
            ),
          ),
        ),
        const SizedBox(height: 6),
        // The legend always has room for the longest one, so every card is the same height whatever its slices.
        SizedBox(
          height: _legendRows * _legendRow,
          child: Column(children: [
            for (final s in ring.slices)
              SizedBox(
                height: _legendRow,
                child: Row(children: [
                  Container(width: 11, height: 11, decoration: BoxDecoration(color: _colourOf(s, palette), shape: BoxShape.circle)),
                  const SizedBox(width: 8),
                  Expanded(child: Text(s.label, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: palette.onNav, fontSize: 13.5))),
                  const SizedBox(width: 10),
                  Text(
                    total == 0 ? '0' : '${_fmt(s.value)}  ·  ${(s.value * 100 / total).round()}%',
                    style: TextStyle(color: palette.onNav, fontSize: 13.5, fontWeight: FontWeight.w700),
                  ),
                ]),
              ),
          ]),
        ),
      ]),
    );
  }

  static const _legendRows = 5, _legendRow = 25.0;

  static String _fmt(double v) => v == v.roundToDouble() ? '${v.toInt()}' : v.toStringAsFixed(1);
}

/// A thin band, the same size for every ring, with a spoke at each slice edge. It draws a bare track when there is nothing to show.
class _RingPainter extends CustomPainter {
  _RingPainter({required this.slices, required this.progress, required this.palette, required this.track});

  final List<RingSlice> slices;
  final double progress;
  final KintsugiPalette palette;
  final Color track;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final w = size.width;
    final rBase = w * 0.285;
    const band = 14.0;
    const gap = 0.05;

    canvas.drawCircle(c, rBase, Paint()..style = PaintingStyle.stroke..strokeWidth = 2..color = track);

    final total = slices.fold<double>(0, (a, s) => a + s.value);
    if (total <= 0) return;
    var start = -math.pi / 2;
    for (final s in slices) {
      final colour = _colourOf(s, palette);
      final span = s.value / total * 2 * math.pi;
      final sweep = math.max(0.0, span * progress - (slices.length > 1 ? gap : 0));
      final r = rBase + w * 0.075;
      final rect = Rect.fromCircle(center: c, radius: r);
      canvas.drawArc(rect, start, sweep, false, Paint()..style = PaintingStyle.stroke..strokeWidth = band..color = colour);
      if (slices.length > 1) {
        final spoke = Paint()..style = PaintingStyle.stroke..strokeWidth = 1.4..color = colour.withValues(alpha: 0.8);
        for (final angle in [start, start + sweep]) {
          final d = Offset(math.cos(angle), math.sin(angle));
          canvas.drawLine(c + d * (rBase - band / 2 - 4), c + d * (r - band / 2), spoke);
        }
      }
      start += span;
    }
  }

  @override
  bool shouldRepaint(_RingPainter old) => old.progress != progress || old.slices != slices || old.palette != palette;
}
