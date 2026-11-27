import 'dart:math' as math;
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:go_router/go_router.dart';
import 'package:intl/intl.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/autonomy.dart';

/// The least height both cards get when they sit side by side, so the calendar is as tall as the chart. It is the
/// chart's natural height without the coverage warning (318 to 388px); with the warning the chart is taller and the
/// calendar stays at this height. Stacked, neither card is padded.
const kevRowHeight = 390.0;

/// Patch Tuesday (the second Tuesday of the month) for every month between [from] and [to], both days included.
List<DateTime> patchTuesdays(DateTime from, DateTime to) {
  final start = DateTime.utc(from.year, from.month, from.day);
  final end = DateTime.utc(to.year, to.month, to.day);
  final days = <DateTime>[];
  for (var month = DateTime.utc(start.year, start.month); !month.isAfter(end); month = DateTime.utc(month.year, month.month + 1)) {
    final firstTuesday = 1 + (DateTime.tuesday - month.weekday) % 7;
    final day = DateTime.utc(month.year, month.month, firstTuesday + 7);
    if (!day.isBefore(start) && !day.isAfter(end)) days.add(day);
  }
  return days;
}

/// A round axis for a chart whose highest value is [highest]: three steps of 1, 2 or 5 times a power of ten.
({double max, double step}) kevAxis(int highest) {
  final raw = math.max(highest, 3) / 3;   // at least a step of 1: a count of devices has no halves
  final magnitude = math.pow(10, (math.log(raw) / math.ln10).floor()).toDouble();
  final step = [1.0, 2.0, 5.0, 10.0].map((m) => m * magnitude).firstWhere((s) => s >= raw);
  return (max: step * 3, step: step);
}

/// Which point a pointer [dx] pixels from the plot's left edge is over, for a plot [width] pixels wide. The chart
/// draws points by date, so a missed day leaves a gap: the position is turned into a date and the nearest point wins.
int kevIndexAt(double dx, double width, List<DateTime> days) {
  if (days.length <= 1 || width <= 0) return 0;
  final span = math.max(days.last.difference(days.first).inDays, 1);
  final at = (dx / width).clamp(0.0, 1.0) * span;
  var best = 0;
  for (var i = 1; i < days.length; i++) {
    if ((days[i].difference(days.first).inDays - at).abs() < (days[best].difference(days.first).inDays - at).abs()) best = i;
  }
  return best;
}

/// Fewer than half of the applications and operating systems have a confirmed CPE, so the counts are a floor.
bool kevCoverageLow(int assessed, int notAssessed) => notAssessed > assessed;

/// Devices with an exploited (CISA KEV) CVE still open, per day: the ones within the 14-day target stacked on the
/// overdue ones, with Patch Tuesday marked, today's figures beside the chart and the CVE on the most devices under it.
/// Sits beside the CVE calendar on the navy panel, in the dark palette's colours like the rings.
///
/// It starts with one day and fills one day at a time: nothing before the first record is invented. When most of the
/// fleet has not been assessed it says so, because a low count from a barely-assessed fleet is not a clean fleet.
class KevBacklogChart extends StatefulWidget {
  const KevBacklogChart({super.key, required this.backlog, this.assessed, this.notAssessed, this.minHeight = 0});

  final KevBacklog backlog;

  /// Applications and operating systems with a confirmed CPE, and without, from the CVE screen's summary. Null when
  /// that could not be read.
  final int? assessed;
  final int? notAssessed;
  final double minHeight;

  @override
  State<KevBacklogChart> createState() => _KevBacklogChartState();
}

class _KevBacklogChartState extends State<KevBacklogChart> {
  int? _hover;

  static const _left = 34.0, _right = 6.0;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    const dark = KintsugiPalette.dark;
    final points = widget.backlog.points;
    final today = widget.backlog.today;
    final axis = kevAxis(points.map((p) => p.devicesOpen).fold(0, math.max));
    final tuesdays = patchTuesdays(points.first.dateUtc, points.last.dateUtc);
    final lowCoverage = widget.assessed != null && widget.notAssessed != null && kevCoverageLow(widget.assessed!, widget.notAssessed!);
    final label = TextStyle(color: palette.navMuted, fontSize: 12);

    return Semantics(
      container: true,
      label: 'KEV backlog: ${today.devicesOpen} devices with an exploited CVE open today, ${today.overdue} overdue, '
          '${today.withinTarget} within target.',
      child: Container(
        key: const Key('kev-backlog'),
        constraints: BoxConstraints(minHeight: widget.minHeight),
        padding: const EdgeInsets.fromLTRB(18, 14, 18, 12),
        decoration: BoxDecoration(
          color: palette.nav,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: palette.navLine.withValues(alpha: 0.5)),
        ),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Text('KEV backlog', style: TextStyle(color: palette.onNav, fontSize: 16, fontWeight: FontWeight.w700)),
          Text(
              'Devices with an exploited CVE still open, per day. '
              '${widget.backlog.target.isEmpty ? 'Target: fixed within 14 days of CISA adding it.' : widget.backlog.target}',
              style: TextStyle(color: palette.navMuted, fontSize: 12.5)),
          const SizedBox(height: 10),
          Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Expanded(
              child: SizedBox(
                height: 170,
                child: LayoutBuilder(builder: (context, box) {
                  final plotWidth = box.maxWidth - _left - _right;
                  return MouseRegion(
                    onHover: (e) => setState(() => _hover = kevIndexAt(e.localPosition.dx - _left, plotWidth, [for (final p in points) p.dateUtc])),
                    onExit: (_) => setState(() => _hover = null),
                    child: CustomPaint(
                      size: Size(box.maxWidth, 170),
                      painter: _KevPainter(
                        points: points,
                        axis: axis,
                        tuesdays: tuesdays,
                        hover: _hover,
                        overdue: dark.red,
                        within: dark.amber,
                        grid: palette.navLine.withValues(alpha: 0.5),
                        ink: palette.onNav,
                        muted: palette.navMuted,
                        card: palette.nav,
                      ),
                    ),
                  );
                }),
              ),
            ),
            const SizedBox(width: 12),
            SizedBox(
              width: 140,
              child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                _Figure(value: '${today.withinTarget}', words: 'within target', colour: dark.amber, key: const Key('kev-within')),
                if (today.dueWithin7Days > 0) ...[
                  const SizedBox(height: 6),
                  Text('+${today.dueWithin7Days} due', key: const Key('kev-due'), style: TextStyle(color: dark.red, fontSize: 13, fontWeight: FontWeight.w700)),
                  Text('turn overdue in the next 7 days', style: label.copyWith(fontSize: 11)),
                ],
                const SizedBox(height: 6),
                _Figure(value: '${today.overdue}', words: 'overdue', colour: dark.red, key: const Key('kev-overdue')),
                if (today.overdueNotSeen7Days > 0)
                  Text('${today.overdueNotSeen7Days} not seen in 7+ days', key: const Key('kev-not-seen'), style: label.copyWith(fontSize: 11)),
              ]),
            ),
          ]),
          if (points.length == 1)
            Padding(
              padding: const EdgeInsets.only(top: 4),
              child: Text('Recording started today. The chart fills in one day at a time.', key: const Key('kev-first-day'), style: label),
            ),
          if (today.topCveId != null)
            InkWell(
              key: const Key('kev-top-cve'),
              onTap: () => context.go(Routes.vulnerabilitiesFor(cve: today.topCveId)),
              child: Padding(
                padding: const EdgeInsets.symmetric(vertical: 6),
                child: Text.rich(TextSpan(children: [
                  TextSpan(text: 'On the most devices: ', style: label),
                  TextSpan(text: '${today.topCveId} · ${today.topCveDevices} devices', style: TextStyle(color: palette.onNav, fontSize: 12.5, fontWeight: FontWeight.w700)),
                  TextSpan(text: '  open CVE', style: TextStyle(color: dark.green, fontSize: 12)),
                ])),
              ),
            ),
          if (lowCoverage)
            Text(
              'Only ${widget.assessed} of ${widget.assessed! + widget.notAssessed!} applications and operating systems have been assessed, so these counts are a floor, not the fleet’s backlog.',
              key: const Key('kev-coverage'),
              style: TextStyle(color: dark.amber, fontSize: 12),
            ),
        ]),
      ),
    );
  }
}

class _Figure extends StatelessWidget {
  const _Figure({super.key, required this.value, required this.words, required this.colour});

  final String value;
  final String words;
  final Color colour;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    return Row(crossAxisAlignment: CrossAxisAlignment.baseline, textBaseline: TextBaseline.alphabetic, children: [
      Text(value, style: TextStyle(color: colour, fontSize: 24, fontWeight: FontWeight.w700)),
      const SizedBox(width: 6),
      Flexible(child: Text(words, style: TextStyle(color: palette.navMuted, fontSize: 12))),
    ]);
  }
}

class _KevPainter extends CustomPainter {
  _KevPainter({
    required this.points,
    required this.axis,
    required this.tuesdays,
    required this.hover,
    required this.overdue,
    required this.within,
    required this.grid,
    required this.ink,
    required this.muted,
    required this.card,
  });

  final List<KevBacklogPoint> points;
  final ({double max, double step}) axis;
  final List<DateTime> tuesdays;
  final int? hover;
  final Color overdue, within, grid, ink, muted, card;

  static const _left = 34.0, _right = 6.0, _top = 6.0, _bottom = 18.0;

  void _text(Canvas canvas, String text, Offset at, {double size = 10.5, Color? color, bool right = false, bool bold = false}) {
    final tp = TextPainter(
      text: TextSpan(text: text, style: TextStyle(color: color ?? muted, fontSize: size, fontWeight: bold ? FontWeight.w700 : FontWeight.w400)),
      textDirection: ui.TextDirection.ltr,
    )..layout();
    tp.paint(canvas, Offset(right ? at.dx - tp.width : at.dx, at.dy));
  }

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width - _left - _right, h = size.height - _top - _bottom;
    double y(num v) => _top + h * (1 - v / axis.max);
    final first = points.first.dateUtc, span = math.max(points.last.dateUtc.difference(first).inDays, 1);
    double x(KevBacklogPoint p) => points.length == 1 ? _left + w / 2 : _left + w * p.dateUtc.difference(first).inDays / span;

    final gridPaint = Paint()
      ..color = grid
      ..strokeWidth = 1;
    for (var i = 0; i <= 3; i++) {
      final v = axis.step * i;
      canvas.drawLine(Offset(_left, y(v)), Offset(size.width - _right, y(v)), gridPaint);
      _text(canvas, v.round().toString(), Offset(_left - 6, y(v) - 6), right: true);
    }

    if (points.length == 1) {
      final p = points.first, cx = x(p), half = 16.0;
      canvas.drawRect(Rect.fromLTRB(cx - half, y(p.overdue), cx + half, y(0)), Paint()..color = overdue);
      canvas.drawRect(Rect.fromLTRB(cx - half, y(p.devicesOpen), cx + half, y(p.overdue)), Paint()..color = within.withValues(alpha: 0.35));
    } else {
      final below = Path()..moveTo(x(points.first), y(0));
      for (final p in points) {
        below.lineTo(x(p), y(p.overdue));
      }
      below
        ..lineTo(x(points.last), y(0))
        ..close();
      final above = Path()..moveTo(x(points.first), y(points.first.overdue));
      for (final p in points) {
        above.lineTo(x(p), y(p.devicesOpen));
      }
      for (final p in points.reversed) {
        above.lineTo(x(p), y(p.overdue));
      }
      above.close();
      canvas.drawPath(above, Paint()..color = within.withValues(alpha: 0.30));
      canvas.drawPath(below, Paint()..color = overdue);
      final line = Path()..moveTo(x(points.first), y(points.first.devicesOpen));
      for (final p in points) {
        line.lineTo(x(p), y(p.devicesOpen));
      }
      canvas.drawPath(
        line,
        Paint()
          ..color = within
          ..style = PaintingStyle.stroke
          ..strokeWidth = 2
          ..strokeJoin = StrokeJoin.round,
      );
    }

    // Patch Tuesday, dashed; a label only where it clears the one before.
    var lastLabel = -1000.0;
    for (final day in tuesdays) {
      final px = points.length == 1 ? _left + w / 2 : _left + w * day.difference(first).inDays / span;
      for (var yy = _top; yy < y(0); yy += 6) {
        canvas.drawLine(Offset(px, yy), Offset(px, math.min(yy + 3, y(0))), Paint()..color = muted..strokeWidth = 1);
      }
      if (px - lastLabel > 90) {
        _text(canvas, 'PATCH TUESDAY', Offset(px + 3, _top), size: 9.5);
        lastLabel = px;
      }
    }

    final fmt = DateFormat('d MMM');
    _text(canvas, fmt.format(points.first.dateUtc), Offset(_left, size.height - 14));
    if (points.length > 1) _text(canvas, fmt.format(points.last.dateUtc), Offset(size.width - _right, size.height - 14), right: true);

    final i = hover;
    if (i != null && i < points.length) {
      final p = points[i], px = x(p);
      canvas.drawLine(Offset(px, _top), Offset(px, y(0)), Paint()..color = ink.withValues(alpha: 0.6)..strokeWidth = 1);
      canvas.drawCircle(Offset(px, y(p.devicesOpen)), 4, Paint()..color = within);
      final lines = [DateFormat('d MMM yyyy').format(p.dateUtc), '${p.devicesOpen} open', '${p.withinTarget} within target', '${p.overdue} overdue'];
      const boxW = 118.0, boxH = 62.0;
      final bx = px + 8 + boxW > size.width ? px - 8 - boxW : px + 8;
      canvas.drawRRect(RRect.fromRectAndRadius(Rect.fromLTWH(bx, _top + 4, boxW, boxH), const Radius.circular(6)), Paint()..color = card);
      canvas.drawRRect(
        RRect.fromRectAndRadius(Rect.fromLTWH(bx, _top + 4, boxW, boxH), const Radius.circular(6)),
        Paint()..color = grid..style = PaintingStyle.stroke,
      );
      for (var n = 0; n < lines.length; n++) {
        _text(canvas, lines[n], Offset(bx + 8, _top + 9 + n * 14), color: n == 0 ? ink : muted, bold: n == 0, size: 11);
      }
    }
  }

  @override
  bool shouldRepaint(_KevPainter old) => old.points != points || old.hover != hover || old.axis != axis || old.overdue != overdue;
}
