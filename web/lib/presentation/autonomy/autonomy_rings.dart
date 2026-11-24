import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/autonomy.dart';
import 'autonomy_model.dart';

/// Three rings under the key figures, for a fast look: devices up to date or failing, device health, and
/// what the delay costs. Each is a number in the middle with slices around it, and the words are in the
/// legend beside it, so a colour is never the only signal.
///
/// They sit on a navy panel in both themes (the sidebar's navy) and use the dark palette's colours, which
/// are the ones made for a dark ground; the light palette's green and amber are dark text colours and would
/// vanish on it. A slice reaches further out the larger it is, so the biggest reads first.
class AutonomyRings extends StatelessWidget {
  const AutonomyRings({super.key, required this.data});

  final Autonomy data;

  @override
  Widget build(BuildContext context) {
    final rings = ringsFor(data);
    return Wrap(
      spacing: 12,
      runSpacing: 12,
      children: [
        for (final ring in rings) SizedBox(width: 340, child: _RingCard(ring: ring)),
      ],
    );
  }
}

Color _colourOf(RingKind kind) {
  const dark = KintsugiPalette.dark;
  return switch (kind) {
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
        Text(ring.title, style: TextStyle(color: palette.onNav, fontSize: 16, fontWeight: FontWeight.w700)),
        Text(ring.subtitle, style: TextStyle(color: palette.navMuted, fontSize: 12.5)),
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
                  painter: _GlowRingPainter(slices: slices, progress: t, ink: palette.onNav, track: palette.navLine.withValues(alpha: 0.35)),
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
        for (final s in ring.slices)
          Padding(
            padding: const EdgeInsets.symmetric(vertical: 3),
            child: Row(children: [
              Container(width: 11, height: 11, decoration: BoxDecoration(color: _colourOf(s.kind), shape: BoxShape.circle)),
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
    );
  }

  static String _fmt(double v) => v == v.roundToDouble() ? '${v.toInt()}' : v.toStringAsFixed(1);
}

/// A thin glowing band whose slices reach out by share, joined by spokes, inside a ring of dots and fine
/// lines. It draws a bare track when there is nothing to show.
class _GlowRingPainter extends CustomPainter {
  _GlowRingPainter({required this.slices, required this.progress, required this.ink, required this.track});

  final List<RingSlice> slices;
  final double progress;
  final Color ink, track;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final w = size.width;
    final rBase = w * 0.285;
    const band = 14.0;
    const gap = 0.05;

    canvas.drawCircle(c, rBase, Paint()..style = PaintingStyle.stroke..strokeWidth = 2..color = track);

    // The network ring. Its jitter is fixed, so it does not shimmer from one frame to the next.
    const n = 64;
    final mesh = <Offset>[
      for (var i = 0; i < n; i++)
        c + Offset(math.cos(i / n * 2 * math.pi), math.sin(i / n * 2 * math.pi)) * (w * 0.455 + math.sin(i * 12.9898) * 7 + (i.isEven ? 3 : -3)),
    ];
    final line = Paint()..style = PaintingStyle.stroke..strokeWidth = 0.8..color = ink.withValues(alpha: 0.2);
    final dot = Paint()..color = ink.withValues(alpha: 0.45);
    for (var i = 0; i < n; i++) {
      canvas.drawLine(mesh[i], mesh[(i + 1) % n], line);
      canvas.drawLine(mesh[i], mesh[(i + 3) % n], line);
      canvas.drawCircle(mesh[i], i % 4 == 0 ? 2.4 : 1.5, dot);
    }

    final total = slices.fold<double>(0, (a, s) => a + s.value);
    if (total <= 0) return;
    final biggest = slices.map((s) => s.value).reduce(math.max);
    var start = -math.pi / 2;
    for (final s in slices) {
      final colour = _colourOf(s.kind);
      final span = s.value / total * 2 * math.pi;
      final sweep = math.max(0.0, span * progress - (slices.length > 1 ? gap : 0));
      final r = rBase + w * 0.075 * (s.value / biggest);
      final rect = Rect.fromCircle(center: c, radius: r);
      canvas.drawArc(rect, start, sweep, false,
          Paint()..style = PaintingStyle.stroke..strokeWidth = band + 8..color = colour.withValues(alpha: 0.5)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 12));
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
  bool shouldRepaint(_GlowRingPainter old) => old.progress != progress || old.slices != slices || old.ink != ink;
}
