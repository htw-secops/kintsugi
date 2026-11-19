import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../../domain/entities/host.dart';

/// How long ago [when] was, the way NinjaOne's Last Uptime column says it: "28 minutes ago",
/// "3 days ago", "a month ago". Null for a host that has never checked in.
///
/// [now] is a parameter only so a test can pin the clock.
String? hostRelativeAgo(DateTime? when, {DateTime? now}) {
  if (when == null) return null;
  final minutes = (now ?? DateTime.now()).toUtc().difference(when.toUtc()).inMinutes;
  if (minutes < 1) return 'just now';
  if (minutes < 60) return minutes == 1 ? 'a minute ago' : '$minutes minutes ago';
  final hours = minutes ~/ 60;
  if (hours < 24) return hours == 1 ? 'an hour ago' : '$hours hours ago';
  final days = hours ~/ 24;
  if (days < 30) return days == 1 ? 'a day ago' : '$days days ago';
  final months = days ~/ 30;
  if (months < 12) return months == 1 ? 'a month ago' : '$months months ago';
  final years = days ~/ 365;
  return years <= 1 ? 'a year ago' : '$years years ago';
}

/// The status-chip key each health state is drawn with: the palette's green, amber and red.
String hostHealthChipKey(HostHealthState state) => switch (state) {
      HostHealthState.healthy => 'up-to-date',
      HostHealthState.pending => 'update-available',
      HostHealthState.problem => 'patch-failed',
    };

/// The colour of a health state, for the tiles, the legend and the bars.
Color hostHealthColor(KintsugiPalette palette, HostHealthState state) => switch (state) {
      HostHealthState.healthy => palette.green,
      HostHealthState.pending => palette.amber,
      HostHealthState.problem => palette.red,
    };

/// Puts [text] on the clipboard and says so for a moment.
///
/// Used for every copy icon on the page (hostname, serial, and the person's details once revealed),
/// so they all behave and read the same.
Future<void> copyHostValue(BuildContext context, String text) async {
  await Clipboard.setData(ClipboardData(text: text));
  if (!context.mounted) return;
  final messenger = ScaffoldMessenger.maybeOf(context);
  messenger
    ?..hideCurrentSnackBar()
    ..showSnackBar(
      SnackBar(
        content: Text('Copied $text'),
        duration: const Duration(milliseconds: 2200),
        behavior: SnackBarBehavior.floating,
        width: 360,
      ),
    );
}
