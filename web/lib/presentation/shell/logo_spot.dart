import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../settings/appearance_cubit.dart';

/// The logo at the top of the sidebar.
///
/// It shows the HTW logo, or a company's own when one is set on Settings > Appearance, and takes
/// turns with the Kintsugi logo, [interval] apart, by [animation]: `spin` (the tile turns like a
/// coin), `fade` (a cross-fade) or `off` (no rotation, the HTW or company logo only). It always
/// starts on the HTW or company logo, so that is what is seen first after every page load. A system
/// "reduce motion" setting turns the spin into a fade, and the pointer being over the tile holds the
/// current logo still.
///
/// The Kintsugi logo is the product's own artwork and is never altered. A company's logo is drawn on
/// a light plate, because the sidebar is navy in both themes and an uploaded PNG is as likely to be
/// dark lettering as light; the HTW logo is the reverse artwork made for navy and needs none.
class LogoSpot extends StatefulWidget {
  const LogoSpot({
    super.key,
    required this.organisation,
    required this.animation,
    this.interval = const Duration(seconds: 8),
  });

  final Organisation organisation;
  final String animation;
  final Duration interval;

  /// The tile's size. Fixed, so the sidebar below it does not move while the logos take turns.
  static const size = Size(160, 72);

  @override
  State<LogoSpot> createState() => _LogoSpotState();
}

class _LogoSpotState extends State<LogoSpot> {
  Timer? _timer;
  bool _kintsugi = false;
  double _turns = 0;
  bool _hover = false;

  bool get _rotates => widget.animation != 'off';

  @override
  void initState() {
    super.initState();
    _restart();
  }

  @override
  void didUpdateWidget(LogoSpot old) {
    super.didUpdateWidget(old);
    if (old.animation != widget.animation) _restart();
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  void _restart() {
    _timer?.cancel();
    _timer = null;
    if (!_rotates) {
      _kintsugi = false;
      _turns = 0;
      return;
    }
    _timer = Timer.periodic(widget.interval, (_) {
      if (_hover || !mounted) return;
      setState(() {
        _kintsugi = !_kintsugi;
        _turns += 1;
      });
    });
  }

  @override
  Widget build(BuildContext context) {
    final logo = widget.organisation.logoPng;
    final spin = widget.animation == 'spin' && !MediaQuery.disableAnimationsOf(context);

    // The HTW logo, or the company's on a light plate.
    final brand = logo == null
        ? Image.asset(
            'assets/img/htw-logo-reverse.png',
            key: const Key('logo-spot-htw'),
            fit: BoxFit.contain,
            semanticLabel: 'Herron Todd White',
          )
        : Container(
            key: const Key('logo-spot-company'),
            padding: const EdgeInsets.all(6),
            decoration: BoxDecoration(
              color: KintsugiPalette.light.surface,
              borderRadius: BorderRadius.circular(8),
            ),
            child: Image.memory(logo, fit: BoxFit.contain, errorBuilder: (_, _, _) => const SizedBox.shrink()),
          );
    final kintsugi = ClipRRect(
      key: const Key('logo-spot-kintsugi'),
      borderRadius: BorderRadius.circular(12),
      child: Image.asset(
        'assets/img/logo-nav.png',
        width: LogoSpot.size.height,
        height: LogoSpot.size.height,
        fit: BoxFit.cover,
        semanticLabel: 'Kintsugi',
      ),
    );
    Widget framed(Widget face) => SizedBox.fromSize(size: LogoSpot.size, child: Center(child: face));

    final Widget tile;
    if (!_rotates) {
      tile = framed(brand);
    } else if (spin) {
      tile = TweenAnimationBuilder<double>(
        tween: Tween<double>(end: _turns),
        duration: const Duration(milliseconds: 900),
        curve: Curves.easeInOut,
        builder: (context, turns, _) {
          final angle = turns * math.pi;
          // The far side is drawn turned half a revolution, so it reads the right way round once the
          // tile has passed edge-on.
          final front = math.cos(angle) >= 0;
          return Transform(
            alignment: Alignment.center,
            transform: Matrix4.identity()
              ..setEntry(3, 2, 0.0012)
              ..rotateY(angle),
            child: front
                ? framed(brand)
                : Transform(alignment: Alignment.center, transform: Matrix4.rotationY(math.pi), child: framed(kintsugi)),
          );
        },
      );
    } else {
      tile = AnimatedSwitcher(
        duration: const Duration(milliseconds: 700),
        child: KeyedSubtree(key: ValueKey(_kintsugi), child: framed(_kintsugi ? kintsugi : brand)),
      );
    }

    return Align(
      alignment: Alignment.centerLeft,
      child: MouseRegion(
        onEnter: (_) => _hover = true,
        onExit: (_) => _hover = false,
        child: tile,
      ),
    );
  }
}
