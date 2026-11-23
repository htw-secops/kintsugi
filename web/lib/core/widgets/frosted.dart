import 'dart:ui';

import 'package:flutter/widgets.dart';

import '../theme/kintsugi_palette.dart';

/// Says that a company wallpaper is showing behind the app, so surfaces should be frosted glass
/// instead of near-solid sheets: the picture stays visible between and behind them, and the text on
/// them sits on a tinted, blurred, even background whatever the picture looks like underneath.
///
/// An inherited widget in `core/` rather than a read of the appearance cubit, so a panel keeps
/// working in a test with no cubit above it, and `core/` does not depend on `presentation/`.
class Frosted extends InheritedWidget {
  const Frosted({super.key, required this.enabled, required super.child});

  final bool enabled;

  /// False when nothing above says otherwise.
  static bool of(BuildContext context) => context.dependOnInheritedWidgetOfExactType<Frosted>()?.enabled ?? false;

  @override
  bool updateShouldNotify(Frosted oldWidget) => enabled != oldWidget.enabled;
}

/// The glass itself: [child] over a blurred backdrop, tinted with the page colour.
///
/// 60% page colour and a 14px blur: tested against a busy photo at the sizes of the smallest text
/// in the app (12.8px hint text), in both themes. Less tint and small text starts to fight the picture.
class FrostedSurface extends StatelessWidget {
  const FrostedSurface({super.key, required this.child, this.radius = BorderRadius.zero, this.decoration});

  final Widget child;
  final BorderRadius radius;

  /// Border and shadow to draw on top of the tint. Its colour is replaced.
  final BoxDecoration? decoration;

  @override
  Widget build(BuildContext context) => ClipRRect(
        borderRadius: radius,
        child: BackdropFilter(
          filter: ImageFilter.blur(sigmaX: 14, sigmaY: 14),
          child: DecoratedBox(
            decoration: (decoration ?? const BoxDecoration()).copyWith(
              color: context.palette.background.withValues(alpha: 0.60),
              borderRadius: radius,
            ),
            child: child,
          ),
        ),
      );
}
