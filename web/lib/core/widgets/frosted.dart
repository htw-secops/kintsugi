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

/// How much of the page colour covers a wallpaper, under the glass. `AppShell` draws it.
const frostedCover = 0.30;

/// The least a text colour may contrast with the glass, over any picture.
const frostedMinContrast = 4.5;

/// How opaque the glass must be so [KintsugiPalette.text], `muted` and `action` read at 4.5:1 over the worst
/// picture there is.
///
/// A picture can only be as dark as black or as light as white, and contrast moves one way with the background's
/// lightness, so those two pictures are the worst cases for any picture. The glass is the page colour at `alpha`
/// over the picture under its own [frostedCover]; the opacity starts at 60%, which is what a mid-tone photo needs,
/// and rises until both extremes hold. At 100% the glass is the page itself, which `withAccent` already tests.
double frostedAlpha(KintsugiPalette palette) {
  final bg = palette.background;
  Color over(Color picture, double alpha) =>
      Color.alphaBlend(bg.withValues(alpha: alpha), Color.alphaBlend(bg.withValues(alpha: frostedCover), picture));
  bool holds(double alpha) {
    for (final picture in [const Color(0xFF000000), const Color(0xFFFFFFFF)]) {
      final glass = over(picture, alpha);
      for (final ink in [palette.text, palette.muted, palette.action]) {
        if (KintsugiPalette.contrastRatio(ink, glass) < frostedMinContrast) return false;
      }
    }
    return true;
  }

  for (var alpha = 0.60; alpha < 1.0; alpha += 0.02) {
    if (holds(alpha)) return alpha;
  }
  return 1.0;
}

/// The glass itself: [child] over a blurred backdrop, tinted with the page colour.
///
/// At least 60% page colour (see [frostedAlpha]) and a 14px blur. The tint rises above 60% only as far as it
/// must for the text colours to keep 4.5:1 over a black or a white picture.
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
              color: context.palette.background.withValues(alpha: frostedAlpha(context.palette)),
              borderRadius: radius,
            ),
            child: child,
          ),
        ),
      );
}
