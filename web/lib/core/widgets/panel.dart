import 'package:flutter/material.dart';

import '../theme/kintsugi_palette.dart';
import 'frosted.dart';

/// The surface everything on a screen sits inside — the guide's card: white (the dark surface in
/// the dark theme), a hairline, the 12px radius and the card shadow.
class KintsugiPanel extends StatelessWidget {
  const KintsugiPanel({super.key, required this.child, this.padding, this.clip = true});

  final Widget child;
  final EdgeInsetsGeometry? padding;

  /// Whether to clip to the rounded corners. Tables want this so a row's hover tint does not
  /// square off the panel; a panel holding an overflowing menu does not.
  final bool clip;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    if (Frosted.of(context)) {
      // Over a wallpaper: glass instead of a near-solid sheet. Always clipped, as the blur needs it.
      return FrostedSurface(
        radius: BorderRadius.circular(12),
        decoration: BoxDecoration(border: Border.all(color: palette.border), boxShadow: palette.shadow),
        child: Padding(padding: padding ?? EdgeInsets.zero, child: child),
      );
    }
    return Container(
      clipBehavior: clip ? Clip.antiAlias : Clip.none,
      padding: padding,
      decoration: BoxDecoration(
        color: palette.surface,
        border: Border.all(color: palette.border),
        borderRadius: BorderRadius.circular(12),
        boxShadow: palette.shadow,
      ),
      child: child,
    );
  }
}

/// A panel whose only content is a sentence explaining why it is empty — `.empty`.
class EmptyPanel extends StatelessWidget {
  const EmptyPanel(this.message, {super.key, this.trailing});

  final String message;
  final Widget? trailing;

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 40),
        child: Column(
          children: [
            Text(
              message,
              textAlign: TextAlign.center,
              style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: context.palette.muted),
            ),
            if (trailing != null) ...[const SizedBox(height: 12), trailing!],
          ],
        ),
      );
}
