import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';

// The art on the Overview page. All of it is drawn in code, so there is no image to ship or license, and it
// takes its navy and cyan from the palette. Both are the sidebar's colours in either theme, so it reads the
// same in light and dark. It is decoration only: every widget here is hidden from screen readers.

const _white = Color(0xFFEAF8FF);
const _ice = Color(0xFF7FDBFF);
const _ground = Color(0xFF02060F);

/// A network above a row of flow cards: a mesh of straight lines with bright nodes, a wireframe globe over
/// each card, thick straight lines from globe to globe, and a line dropping from each globe into its card.
///
/// [columns] is how many cards the row has room for and [count] how many it holds, so a short last row
/// still lines its globes up with the cards below. [gap] is the space between those cards.
class OverviewNetwork extends StatelessWidget {
  const OverviewNetwork({
    super.key,
    required this.columns,
    required this.count,
    required this.gap,
    required this.seed,
  });

  static const height = 180.0;

  final int columns, count, seed;
  final double gap;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return ExcludeSemantics(
      child: RepaintBoundary(
        child: SizedBox(
          height: height,
          width: double.infinity,
          child: CustomPaint(
            painter: _NetworkPainter(
              columns: columns,
              count: count,
              gap: gap,
              seed: seed,
              base: palette.nav,
              glow: palette.navLine,
            ),
          ),
        ),
      ),
    );
  }
}

class _NetworkPainter extends CustomPainter {
  _NetworkPainter({
    required this.columns,
    required this.count,
    required this.gap,
    required this.seed,
    required this.base,
    required this.glow,
  });

  final int columns, count, seed;
  final double gap;
  final Color base, glow;

  @override
  void paint(Canvas canvas, Size size) {
    final r = math.Random(seed);
    final rect = Offset.zero & size;
    canvas.save();
    canvas.clipRRect(
      RRect.fromRectAndCorners(
        rect,
        topLeft: const Radius.circular(12),
        topRight: const Radius.circular(12),
      ),
    );
    // Darker than the sidebar, like a night sky, with a cool glow in the middle.
    canvas.drawRect(rect, Paint()..color = Color.lerp(base, _ground, 0.55)!);
    canvas.drawRect(
      rect,
      Paint()
        ..shader = RadialGradient(
          radius: 0.9,
          colors: [glow.withValues(alpha: 0.14), glow.withValues(alpha: 0)],
        ).createShader(rect),
    );

    final cardWidth = (size.width - gap * (columns - 1)) / columns;
    final centres = [
      for (var i = 0; i < count; i++)
        Offset(
          i * (cardWidth + gap) + cardWidth / 2,
          size.height * 0.42 + (r.nextDouble() - 0.5) * 24,
        ),
    ];
    final radius = math.min(44.0, cardWidth / 2 - 8);

    // Out-of-focus nodes behind, for depth.
    for (var i = 0; i < 10; i++) {
      final c = Offset(
        r.nextDouble() * size.width,
        r.nextDouble() * size.height,
      );
      canvas.drawCircle(
        c,
        6 + r.nextDouble() * 12,
        Paint()
          ..color = _ice.withValues(alpha: 0.10 + r.nextDouble() * 0.10)
          ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 7),
      );
    }

    // The mesh: scattered nodes, each joined to its nearest neighbours by a straight line.
    final nodes = <Offset>[...centres];
    for (var i = 0; i < (size.width / 14).round(); i++) {
      final p = Offset(
        r.nextDouble() * size.width,
        r.nextDouble() * size.height,
      );
      if (centres.any((c) => (c - p).distance < radius + 4)) continue;
      nodes.add(p);
    }
    final edge = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 0.8;
    final seen = <int>{};
    for (var i = 0; i < nodes.length; i++) {
      final near =
          [
            for (var j = 0; j < nodes.length; j++)
              if (j != i) j,
          ]..sort(
            (x, y) => (nodes[x] - nodes[i]).distanceSquared.compareTo(
              (nodes[y] - nodes[i]).distanceSquared,
            ),
          );
      for (final j in near.take(3)) {
        final key = i < j ? i * 100000 + j : j * 100000 + i;
        if (!seen.add(key) || (nodes[i] - nodes[j]).distance > 110) continue;
        // Brighter along the middle band.
        final lift =
            1 -
            (((nodes[i] + nodes[j]) / 2).dy - size.height / 2).abs() /
                size.height;
        edge.color = _ice.withValues(alpha: 0.18 + 0.4 * lift);
        canvas.drawLine(nodes[i], nodes[j], edge);
      }
    }

    // Thick straight lines from globe to globe, bending at a few bright nodes.
    final halo = Paint()
      ..style = PaintingStyle.stroke
      ..color = glow.withValues(alpha: 0.35)
      ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 5);
    final bright = Paint()
      ..style = PaintingStyle.stroke
      ..strokeCap = StrokeCap.round;
    for (var i = 0; i < count - 1; i++) {
      for (var strand = 0; strand < 2; strand++) {
        final a = centres[i], b = centres[i + 1];
        final hops = 2 + r.nextInt(2);
        final pts = [
          a,
          for (var h = 1; h <= hops; h++)
            Offset.lerp(a, b, h / (hops + 1))! +
                Offset(0, (r.nextDouble() - 0.5) * 60),
          b,
        ];
        final path = Path()..moveTo(a.dx, a.dy);
        for (final q in pts.skip(1)) {
          path.lineTo(q.dx, q.dy);
        }
        final w = strand == 0 ? 1.0 : 0.45;
        canvas.drawPath(path, halo..strokeWidth = 8 * w);
        canvas.drawPath(
          path,
          bright
            ..strokeWidth = 2.2 * w
            ..color = _ice.withValues(alpha: strand == 0 ? 0.95 : 0.55),
        );
        for (final q in pts.skip(1).take(hops)) {
          canvas.drawCircle(
            q,
            5,
            Paint()
              ..color = _white.withValues(alpha: 0.35)
              ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3),
          );
          canvas.drawCircle(q, 2.4, Paint()..color = _white);
        }
      }
    }

    // Bright nodes on the mesh, white or cyan.
    for (final p in nodes.skip(count)) {
      final c = r.nextBool() ? _white : _ice;
      final rad = 1.0 + r.nextDouble() * 1.6;
      canvas.drawCircle(
        p,
        rad * 3,
        Paint()
          ..color = c.withValues(alpha: 0.25)
          ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3),
      );
      canvas.drawCircle(p, rad, Paint()..color = c);
    }

    // The globes: dots on a sphere joined to near neighbours by straight lines, the front brighter than the back.
    for (final c in centres) {
      canvas.drawCircle(
        c,
        radius + 14,
        Paint()
          ..color = glow.withValues(alpha: 0.4)
          ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 16),
      );
      canvas.drawCircle(
        c,
        radius,
        Paint()..color = _ground.withValues(alpha: 0.75),
      );
      final pts = <List<double>>[];
      for (var i = 0; i < 150; i++) {
        final z = r.nextDouble() * 2 - 1,
            a = r.nextDouble() * 2 * math.pi,
            q = math.sqrt(1 - z * z);
        pts.add([q * math.cos(a), q * math.sin(a), z]);
      }
      final line = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 0.7;
      for (var i = 0; i < pts.length; i++) {
        for (var j = i + 1; j < pts.length; j++) {
          final dx = pts[i][0] - pts[j][0],
              dy = pts[i][1] - pts[j][1],
              dz = pts[i][2] - pts[j][2];
          if (dx * dx + dy * dy + dz * dz > 0.16) continue;
          line.color = _ice.withValues(
            alpha: 0.12 + 0.5 * ((pts[i][2] + pts[j][2]) / 4 + 0.5),
          );
          canvas.drawLine(
            c + Offset(pts[i][0], pts[i][1]) * radius,
            c + Offset(pts[j][0], pts[j][1]) * radius,
            line,
          );
        }
      }
      for (final pt in pts) {
        final front = pt[2] * 0.5 + 0.5;
        canvas.drawCircle(
          c + Offset(pt[0], pt[1]) * radius,
          0.8 + front * 1.3,
          Paint()..color = _white.withValues(alpha: 0.35 + 0.65 * front),
        );
      }
      canvas.drawCircle(
        c,
        radius,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1.2
          ..color = _ice.withValues(alpha: 0.8),
      );

      // The straight drop into the card.
      final drop = Path()
        ..moveTo(c.dx, c.dy + radius)
        ..lineTo(c.dx, size.height);
      canvas.drawPath(
        drop,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 8
          ..color = glow.withValues(alpha: 0.35)
          ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 4),
      );
      canvas.drawPath(
        drop,
        Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..color = _ice,
      );
      canvas.drawCircle(Offset(c.dx, size.height), 3, Paint()..color = _white);
    }
    canvas.restore();
  }

  @override
  bool shouldRepaint(_NetworkPainter old) =>
      old.columns != columns ||
      old.count != count ||
      old.gap != gap ||
      old.seed != seed ||
      old.base != base ||
      old.glow != glow;
}

/// The logo at the bottom of a capability card: a small glowing shield with the card's icon in it.
class OverviewLogo extends StatelessWidget {
  const OverviewLogo({super.key, required this.icon});

  final IconData icon;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return ExcludeSemantics(
      child: SizedBox(
        width: 46,
        height: 50,
        child: Stack(
          alignment: Alignment.center,
          children: [
            CustomPaint(
              size: const Size(46, 50),
              painter: _ShieldPainter(base: palette.nav, glow: palette.navLine),
            ),
            Icon(icon, size: 18, color: palette.onNav),
          ],
        ),
      ),
    );
  }
}

class _ShieldPainter extends CustomPainter {
  _ShieldPainter({required this.base, required this.glow});

  final Color base, glow;

  @override
  void paint(Canvas canvas, Size size) {
    final k = size.width / 78;
    final c = size.center(Offset.zero) + Offset(0, k);
    final shield = Path()
      ..moveTo(c.dx, c.dy - 42 * k)
      ..cubicTo(
        c.dx + 14 * k,
        c.dy - 34 * k,
        c.dx + 26 * k,
        c.dy - 32 * k,
        c.dx + 34 * k,
        c.dy - 32 * k,
      )
      ..lineTo(c.dx + 34 * k, c.dy + 4 * k)
      ..cubicTo(
        c.dx + 34 * k,
        c.dy + 24 * k,
        c.dx + 16 * k,
        c.dy + 36 * k,
        c.dx,
        c.dy + 44 * k,
      )
      ..cubicTo(
        c.dx - 16 * k,
        c.dy + 36 * k,
        c.dx - 34 * k,
        c.dy + 24 * k,
        c.dx - 34 * k,
        c.dy + 4 * k,
      )
      ..lineTo(c.dx - 34 * k, c.dy - 32 * k)
      ..cubicTo(
        c.dx - 26 * k,
        c.dy - 32 * k,
        c.dx - 14 * k,
        c.dy - 34 * k,
        c.dx,
        c.dy - 42 * k,
      )
      ..close();
    canvas.drawPath(
      shield,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 5
        ..color = glow.withValues(alpha: 0.7)
        ..maskFilter = const MaskFilter.blur(BlurStyle.normal, 5),
    );
    canvas.drawPath(shield, Paint()..color = base);
    canvas.drawPath(
      shield,
      Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.8
        ..color = glow,
    );
  }

  @override
  bool shouldRepaint(_ShieldPainter old) =>
      old.base != base || old.glow != glow;
}
