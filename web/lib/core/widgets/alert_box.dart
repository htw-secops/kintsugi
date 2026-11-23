import 'package:flutter/material.dart';

import '../theme/kintsugi_palette.dart';

enum AlertKind { success, error, info }

/// A tinted, outlined message block — `.alert`.
///
/// [AlertKind.info] carries most of the weight in this app and is worth understanding: several
/// screens use it to say something out loud that would otherwise be invisible — a derived
/// `AGENT_API_BASE_URL`, a missing script-approval token, scripts awaiting review. Those are notes
/// beside a working screen, not errors in place of one.
class AlertBox extends StatelessWidget {
  const AlertBox(this.message, {super.key, required this.kind, this.child});

  const AlertBox.success(this.message, {super.key, this.child}) : kind = AlertKind.success;
  const AlertBox.error(this.message, {super.key, this.child}) : kind = AlertKind.error;
  const AlertBox.info(this.message, {super.key, this.child}) : kind = AlertKind.info;

  final String message;
  final AlertKind kind;

  /// Extra content under the message — a bulleted list of notes, or a link.
  final Widget? child;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    // The guide's inline banner (`.htw-banner`): the status tint, a 4px rule on the left, an 8px
    // radius. The text is the status text colour, the one pair htw-style tests on its tint in both
    // themes; info is the accent's tint and text, with the Cyan 500 rule the guide gives it.
    final (color, tint, rule) = switch (kind) {
      AlertKind.success => (palette.green, palette.greenTint, palette.green),
      AlertKind.error => (palette.red, palette.redTint, palette.red),
      AlertKind.info => (palette.actionTintText, palette.actionTint, palette.navLine),
    };

    // Clipped rather than given a borderRadius: Flutter will not round a border whose sides differ.
    return Container(
      width: double.infinity,
      margin: const EdgeInsets.only(bottom: 24),
      clipBehavior: Clip.antiAlias,
      decoration: BoxDecoration(borderRadius: BorderRadius.circular(8)),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 14),
        decoration: BoxDecoration(
          color: tint,
          border: Border(left: BorderSide(color: rule, width: 4)),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (message.isNotEmpty)
              Text(message, style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: color)),
            if (child != null) ...[
              if (message.isNotEmpty) const SizedBox(height: 8),
              DefaultTextStyle.merge(
                style: TextStyle(color: color),
                child: child!,
              ),
            ],
          ],
        ),
      ),
    );
  }
}
