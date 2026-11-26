import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:intl/intl.dart';

import '../../core/router/app_router.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/vulnerability.dart';
import 'autonomy_model.dart';

/// How many CVEs get their own slice before the rest are folded into "Other CVEs".
const cveRingNamed = 4;

/// How many weeks the calendar shows.
const cveCalendarWeeks = 52;

/// A CVE needs attention when CISA lists it as exploited or NVD scores it 7.0 or higher. An unscored CVE that
/// is not exploited is an unknown, and is left out rather than guessed at.
bool needsAttention(VulnerabilityFinding f) => f.knownExploited || (f.cvssBaseScore ?? 0) >= 7;

/// How many different applications one CVE reaches. By name, so one app on many hosts, or in many versions, is one.
int applicationsHit(VulnerabilityFinding f) => {for (final s in f.affectedSubjects) s.displayName}.length;

/// " · 500 most-installed of 2183" when the page the screen asked for holds fewer CVEs than the fleet has, else
/// nothing. The ring and the calendar count only what came back, so they have to say so.
String cveLimitNote(int returned, int? total) =>
    total != null && total > returned ? ' · $returned most-installed of $total' : '';

/// The fourth ring: the CVEs that need attention, by how many applications each one reaches.
///
/// The slices add up applications per CVE, so one application with two CVEs is counted under both. That is the
/// point: it says which CVE is worth fixing first, not how many applications are exposed.
RingData cveRing(List<VulnerabilityFinding> findings, {int? total}) {
  final hot = findings.where(needsAttention).toList()
    ..sort((a, b) {
      final byApps = applicationsHit(b).compareTo(applicationsHit(a));
      if (byApps != 0) return byApps;
      if (a.knownExploited != b.knownExploited) return a.knownExploited ? -1 : 1;
      return (b.cvssBaseScore ?? 0).compareTo(a.cvssBaseScore ?? 0);
    });
  final shown = hot.take(cveRingNamed).toList();
  final others = hot.skip(cveRingNamed).map((f) => f.cveId).toList();
  final rest = hot.skip(cveRingNamed).fold<int>(0, (a, f) => a + applicationsHit(f));
  return RingData(
    title: 'CVEs hitting most apps',
    subtitle: 'Exploited or CVSS 7+, by apps reached${cveLimitNote(findings.length, total)}',
    centre: '${hot.length}',
    note: hot.isEmpty ? 'none need attention' : 'need attention',
    slices: [
      for (var i = 0; i < shown.length; i++)
        RingSlice(
          '${shown[i].cveId}${shown[i].knownExploited ? ' (exploited)' : ''}',
          applicationsHit(shown[i]).toDouble(),
          RingKind.other,
          series: i,
          route: Routes.vulnerabilitiesFor(cve: shown[i].cveId),
        ),
      // Opens exactly the CVEs it counts. Too many ids would not fit a request line, so past the limit it leads nowhere.
      if (rest > 0)
        RingSlice('Other CVEs', rest.toDouble(), RingKind.other,
            route: others.length <= Routes.maxCveLinkIds ? Routes.vulnerabilitiesFor(cve: others.join(',')) : null),
    ],
  );
}

DateTime _day(DateTime d) => DateTime.utc(d.year, d.month, d.day);

/// CVEs per day, by the date NVD published them. Only CVEs that affect this fleet are in the list, so a quiet day
/// means nothing relevant was published, not that nothing was.
Map<DateTime, int> cveDayCounts(List<VulnerabilityFinding> findings) {
  final counts = <DateTime, int>{};
  for (final f in findings) {
    final p = f.publishedUtc;
    if (p != null) counts.update(_day(p.toUtc()), (n) => n + 1, ifAbsent: () => 1);
  }
  return counts;
}

/// The grid's first day: the Sunday [cveCalendarWeeks] - 1 weeks before the week [now] is in.
DateTime calendarStart(DateTime now) {
  final today = _day(now.toUtc());
  return today.subtract(Duration(days: today.weekday % 7 + (cveCalendarWeeks - 1) * 7));
}

/// 0 for a day with none, then 1 to 4 by share of the busiest day, so one huge day does not turn the rest blank.
int cveLevel(int count, int max) => count <= 0 || max <= 0 ? 0 : math.max(1, (count * 4 / max).ceil());

/// A thin year-long calendar of CVEs published per day, one square per day, week by week. Sits under the rings and
/// is as wide as them. The count is in each square's tooltip, and the legend gives the scale, so colour is not the
/// only signal.
class CveCalendar extends StatelessWidget {
  const CveCalendar({super.key, required this.findings, required this.now, this.total});

  final List<VulnerabilityFinding> findings;
  final DateTime now;

  /// How many CVEs the fleet has, when more than [findings].
  final int? total;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    const dark = KintsugiPalette.dark;
    final counts = cveDayCounts(findings);
    final start = calendarStart(now);
    final today = _day(now.toUtc());
    // The busiest day among the days drawn: an old day with many CVEs must not pale the visible year.
    final max = counts.entries.where((e) => !e.key.isBefore(start) && !e.key.isAfter(today)).fold<int>(0, (m, e) => math.max(m, e.value));
    Color shade(int level) =>
        level == 0 ? palette.navLine.withValues(alpha: 0.35) : dark.green.withValues(alpha: 0.25 + 0.75 * level / 4);
    const cell = 12.0, gap = 3.0, labelW = 34.0;

    final months = <int, String>{};
    for (var w = 0; w < cveCalendarWeeks; w++) {
      final first = start.add(Duration(days: w * 7));
      // The first column's month is skipped when the next one starts within three weeks: the two labels would overlap.
      final isFirst = w == 0 && start.add(const Duration(days: 21)).month == start.month;
      if (isFirst || (w > 0 && first.month != start.add(Duration(days: (w - 1) * 7)).month)) months[w] = DateFormat('MMM').format(first);
    }

    return Container(
      key: const Key('cve-calendar'),
      padding: const EdgeInsets.fromLTRB(18, 14, 18, 12),
      decoration: BoxDecoration(
        color: palette.nav,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: palette.navLine.withValues(alpha: 0.5)),
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text('CVEs by day', style: TextStyle(color: palette.onNav, fontSize: 16, fontWeight: FontWeight.w700)),
        Text('CVEs that affect this fleet, by the day NVD published them. The last 12 months.${cveLimitNote(findings.length, total)}',
            style: TextStyle(color: palette.navMuted, fontSize: 12.5)),
        const SizedBox(height: 10),
        LayoutBuilder(builder: (context, box) {
          // Each column is a square plus its gap, so the gap comes off the width or the grid runs 52 gaps past the card.
          final w = ((box.maxWidth - labelW) / cveCalendarWeeks - gap).clamp(6.0, 40.0);
          final col = w + gap;
          return SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              SizedBox(
                height: 16,
                width: labelW + cveCalendarWeeks * col,
                child: Stack(children: [
                  for (final e in months.entries)
                    Positioned(left: labelW + e.key * col, child: Text(e.value, style: TextStyle(color: palette.navMuted, fontSize: 11))),
                ]),
              ),
              Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                SizedBox(
                  width: labelW,
                  child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                    for (var r = 0; r < 7; r++)
                      SizedBox(height: cell + gap, child: Text(const {1: 'Mon', 3: 'Wed', 5: 'Fri'}[r] ?? '', style: TextStyle(color: palette.navMuted, fontSize: 10.5))),
                  ]),
                ),
                for (var c = 0; c < cveCalendarWeeks; c++)
                  Column(children: [
                    for (var r = 0; r < 7; r++)
                      Builder(builder: (context) {
                        final day = start.add(Duration(days: c * 7 + r));
                        final future = day.isAfter(today);
                        final n = counts[day] ?? 0;
                        return Tooltip(
                          message: '${DateFormat('d MMM yyyy').format(day)}: $n ${n == 1 ? 'CVE' : 'CVEs'}',
                          child: Container(
                            width: w,
                            height: cell,
                            margin: const EdgeInsets.only(right: gap, bottom: gap),
                            decoration: BoxDecoration(
                              color: future ? Colors.transparent : shade(cveLevel(n, max)),
                              borderRadius: BorderRadius.circular(2),
                            ),
                          ),
                        );
                      }),
                  ]),
              ]),
            ]),
          );
        }),
        const SizedBox(height: 2),
        Row(mainAxisAlignment: MainAxisAlignment.end, children: [
          Text('Fewer', style: TextStyle(color: palette.navMuted, fontSize: 11)),
          const SizedBox(width: 6),
          for (var l = 0; l <= 4; l++)
            Container(width: 12, height: 12, margin: const EdgeInsets.only(right: 3), decoration: BoxDecoration(color: shade(l), borderRadius: BorderRadius.circular(2))),
          const SizedBox(width: 3),
          Text(max > 0 ? 'More (busiest day: $max)' : 'More', style: TextStyle(color: palette.navMuted, fontSize: 11)),
        ]),
      ]),
    );
  }
}
