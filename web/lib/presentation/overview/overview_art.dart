import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';

/// The banner at the top of an Overview card: circuit traces on the sidebar's navy, a glowing shield in
/// the middle and the card's icon inside it. It is decoration only, so it is hidden from screen readers.
///
/// It uses the sidebar's navy and cyan in both themes, like the Autonomy rings. Each card passes its own
/// [seed], so the traces differ from card to card but never change between frames or rebuilds.
class OverviewArt extends StatelessWidget {
  const OverviewArt({super.key, required this.icon, required this.seed, this.linkLeft = false, this.linkRight = false});

  static const height = 120.0;

  final IconData icon;
  final int seed;

  /// A bright trace to that side's edge, where [OverviewLink] carries it on to the next card.
  final bool linkLeft, linkRight;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return ExcludeSemantics(
      child: SizedBox(
        height: height,
        width: double.infinity,
        child: Stack(
          alignment: Alignment.center,
          children: [
            Positioned.fill(
              child: CustomPaint(
                painter: _CircuitPainter(
                  seed: seed,
                  base: palette.nav,
                  glow: palette.navLine,
                  linkLeft: linkLeft,
                  linkRight: linkRight,
                ),
              ),
            ),
            Icon(icon, size: 24, color: palette.onNav),
          ],
        ),
      ),
    );
  }
}

class _CircuitPainter extends CustomPainter {
  _CircuitPainter({required this.seed, required this.base, required this.glow, required this.linkLeft, required this.linkRight});

  final int seed;
  final Color base, glow;
  final bool linkLeft, linkRight;

  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    final rect = Offset.zero & size;
    canvas.drawRect(rect, Paint()..color = base);

    // A faint dot grid, like a board under the traces.
    final grid = Paint()..color = glow.withValues(alpha: 0.14);
    for (var x = 6.0; x < size.width; x += 12) {
      for (var y = 6.0; y < size.height; y += 12) {
        canvas.drawCircle(Offset(x, y), 0.9, grid);
      }
    }
    canvas.drawRect(
      rect,
      Paint()..shader = RadialGradient(colors: [glow.withValues(alpha: 0.5), glow.withValues(alpha: 0)]).createShader(Rect.fromCircle(center: c, radius: size.width * 0.55)),
    );

    // Traces from both sides toward the shield: a run, a 45 degree step, a run, then a pad. Each is drawn
    // twice, a blurred wide stroke under a sharp thin one, so it glows.
    final rnd = math.Random(seed * 7919 + 13);
    final sharp = Paint()..style = PaintingStyle.stroke..strokeWidth = 1.4..color = glow.withValues(alpha: 0.95);
    final soft = Paint()..style = PaintingStyle.stroke..strokeWidth = 4..color = glow.withValues(alpha: 0.4)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3);
    final pad = Paint()..style = PaintingStyle.stroke..strokeWidth = 1.4..color = glow;
    final fill = Paint()..color = glow;
    const rows = 9;
    final mesh = Paint()..style = PaintingStyle.stroke..strokeWidth = 0.8..color = glow.withValues(alpha: 0.35);
    for (final side in [-1.0, 1.0]) {
      Offset? previous;
      for (var i = 0; i < rows; i++) {
        final y = size.height * (i + 0.5) / rows;
        final startX = side < 0 ? 0.0 : size.width;
        final reach = size.width * (0.14 + rnd.nextDouble() * 0.2);
        final step = (rnd.nextBool() ? 1 : -1) * (6 + rnd.nextDouble() * 14);
        final p1 = Offset(startX - side * reach, y);
        final p2 = p1 + Offset(-side * step.abs(), step);
        final p3 = p2 + Offset(-side * size.width * (0.04 + rnd.nextDouble() * 0.08), 0);
        final path = Path()..moveTo(startX, y)..lineTo(p1.dx, p1.dy)..lineTo(p2.dx, p2.dy)..lineTo(p3.dx, p3.dy);
        canvas.drawPath(path, soft);
        canvas.drawPath(path, sharp);
        if (previous != null) canvas.drawLine(previous, p3, mesh);
        previous = p3;
        if (rnd.nextBool()) {
          canvas.drawCircle(p3, 3.6, pad);
          canvas.drawCircle(p3, 1.4, fill);
        } else {
          canvas.drawCircle(p3, 2.4, fill);
        }
      }
    }

    // The chain: a strong trace from the shield out to the edge, ending in a node on the card's border.
    for (final side in [if (linkLeft) -1.0, if (linkRight) 1.0]) {
      final edge = Offset(side < 0 ? 0 : size.width, c.dy);
      final line = Path()..moveTo(c.dx + side * 34, c.dy)..lineTo(edge.dx, edge.dy);
      canvas.drawPath(line, Paint()..style = PaintingStyle.stroke..strokeWidth = 7..color = glow.withValues(alpha: 0.6)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 5));
      canvas.drawPath(line, Paint()..style = PaintingStyle.stroke..strokeWidth = 2.2..color = glow);
      canvas.drawCircle(edge, 4, fill);
    }

    // A ring of dots around the shield.
    final ring = Paint()..color = glow.withValues(alpha: 0.7);
    for (var i = 0; i < 48; i++) {
      final a = i / 48 * 2 * math.pi;
      canvas.drawCircle(c + Offset(math.cos(a), math.sin(a) * 0.95) * 54, i % 4 == 0 ? 1.8 : 1.0, ring);
    }

    // The glowing shield, with a second outline inside it.
    Path shield(double k) => Path()
      ..moveTo(c.dx, c.dy - 42 * k)
      ..cubicTo(c.dx + 14 * k, c.dy - 34 * k, c.dx + 26 * k, c.dy - 32 * k, c.dx + 34 * k, c.dy - 32 * k)
      ..lineTo(c.dx + 34 * k, c.dy + 4 * k)
      ..cubicTo(c.dx + 34 * k, c.dy + 24 * k, c.dx + 16 * k, c.dy + 36 * k, c.dx, c.dy + 44 * k)
      ..cubicTo(c.dx - 16 * k, c.dy + 36 * k, c.dx - 34 * k, c.dy + 24 * k, c.dx - 34 * k, c.dy + 4 * k)
      ..lineTo(c.dx - 34 * k, c.dy - 32 * k)
      ..cubicTo(c.dx - 26 * k, c.dy - 32 * k, c.dx - 14 * k, c.dy - 34 * k, c.dx, c.dy - 42 * k)
      ..close();
    canvas.drawPath(shield(1), Paint()..style = PaintingStyle.stroke..strokeWidth = 9..color = glow.withValues(alpha: 0.65)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 9));
    canvas.drawPath(shield(1), Paint()..color = base.withValues(alpha: 0.9));
    canvas.drawPath(shield(1), Paint()..style = PaintingStyle.stroke..strokeWidth = 2.2..color = glow);
    canvas.drawPath(shield(0.82), Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = glow.withValues(alpha: 0.6));
  }

  @override
  bool shouldRepaint(_CircuitPainter old) => old.seed != seed || old.linkLeft != linkLeft || old.linkRight != linkRight || old.base != base || old.glow != glow;
}

/// The bridge across the gap between two flow cards, level with the banners' middle, so the cards read as
/// one chain of nodes. Decoration only.
class OverviewLink extends StatelessWidget {
  const OverviewLink({super.key, required this.width});

  final double width;

  @override
  Widget build(BuildContext context) => ExcludeSemantics(
        child: SizedBox(
          width: width,
          height: OverviewArt.height + 3,
          child: CustomPaint(painter: _LinkPainter(context.palette.navLine)),
        ),
      );
}

class _LinkPainter extends CustomPainter {
  _LinkPainter(this.glow);

  final Color glow;

  @override
  void paint(Canvas canvas, Size size) {
    const y = 2.5 + OverviewArt.height / 2;
    final line = Path()..moveTo(-2, y)..lineTo(size.width + 2, y);
    canvas.drawPath(line, Paint()..style = PaintingStyle.stroke..strokeWidth = 7..color = glow.withValues(alpha: 0.6)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 5));
    canvas.drawPath(line, Paint()..style = PaintingStyle.stroke..strokeWidth = 2.2..color = glow);
  }

  @override
  bool shouldRepaint(_LinkPainter old) => old.glow != glow;
}
