import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../settings/appearance_cubit.dart';

/// The logo and name at the top of the sidebar.
///
/// Kintsugi's own logo is never changed. A company that has set its own logo sees the two take
/// turns in the same spot, [interval] apart, by [animation]: `spin` (the tile turns like a coin),
/// `fade` (a cross-fade) or `off` (Kintsugi's only). It always starts on Kintsugi's, so that is what
/// is seen first after every page load. A system "reduce motion" setting turns the spin into a fade,
/// and the pointer being over the tile holds the current logo still.
///
/// A name with no logo is a quiet line under "KINTSUGI": there is nothing to take turns with.
class LogoSpot extends StatefulWidget {
  const LogoSpot({super.key, required this.organisation, required this.animation, this.interval = const Duration(seconds: 8)});

  final Organisation organisation;
  final String animation;
  final Duration interval;

  @override
  State<LogoSpot> createState() => _LogoSpotState();
}

class _LogoSpotState extends State<LogoSpot> {
  Timer? _timer;
  bool _company = false;
  double _turns = 0;
  bool _hover = false;

  bool get _alternates => widget.organisation.logoPng != null && widget.animation != 'off';

  @override
  void initState() {
    super.initState();
    _restart();
  }

  @override
  void didUpdateWidget(LogoSpot old) {
    super.didUpdateWidget(old);
    if (old.animation != widget.animation || old.organisation.logoPng != widget.organisation.logoPng) _restart();
  }

  @override
  void dispose() {
    _timer?.cancel();
    super.dispose();
  }

  void _restart() {
    _timer?.cancel();
    _timer = null;
    if (!_alternates) {
      _company = false;
      _turns = 0;
      return;
    }
    _timer = Timer.periodic(widget.interval, (_) {
      if (_hover || !mounted) return;
      setState(() {
        _company = !_company;
        _turns += 1;
      });
    });
  }

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final organisation = widget.organisation;
    final spin = widget.animation == 'spin' && !MediaQuery.disableAnimationsOf(context);

    final kintsugi = ClipRRect(
      borderRadius: BorderRadius.circular(10),
      child: Image.asset('assets/img/logo-nav.png'),
    );
    final company = AspectRatio(
      aspectRatio: 1,
      child: Container(
        padding: const EdgeInsets.all(22),
        decoration: BoxDecoration(
          color: palette.background,
          border: Border.all(color: palette.border),
          borderRadius: BorderRadius.circular(10),
        ),
        child: organisation.logoPng == null
            ? const SizedBox.shrink()
            : Image.memory(organisation.logoPng!, fit: BoxFit.contain, errorBuilder: (_, _, _) => const SizedBox.shrink()),
      ),
    );

    final Widget tile;
    if (!_alternates) {
      tile = kintsugi;
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
                ? kintsugi
                : Transform(alignment: Alignment.center, transform: Matrix4.rotationY(math.pi), child: company),
          );
        },
      );
    } else {
      tile = AnimatedSwitcher(
        duration: const Duration(milliseconds: 700),
        child: KeyedSubtree(key: ValueKey(_company), child: _company ? company : kintsugi),
      );
    }

    final title = _alternates && _company && organisation.name != null && organisation.name!.isNotEmpty
        ? organisation.name!
        : 'KINTSUGI';

    return Column(
      children: [
        MouseRegion(
          onEnter: (_) => _hover = true,
          onExit: (_) => _hover = false,
          child: tile,
        ),
        const SizedBox(height: 10),
        AnimatedSwitcher(
          duration: const Duration(milliseconds: 500),
          child: Text(
            title.toUpperCase(),
            key: ValueKey(title),
            textAlign: TextAlign.center,
            style: AppTheme.display(color: palette.neon, size: 20, weight: FontWeight.w900, letterSpacing: 1.6),
          ),
        ),
        if (!_alternates && organisation.name != null && organisation.name!.isNotEmpty) ...[
          const SizedBox(height: 4),
          Text(
            organisation.name!,
            textAlign: TextAlign.center,
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: palette.text),
          ),
        ],
      ],
    );
  }
}
