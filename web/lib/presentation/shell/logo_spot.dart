import 'package:flutter/material.dart';

import '../../core/theme/kintsugi_palette.dart';
import '../settings/appearance_cubit.dart';

/// The logo at the top of the sidebar: the HTW logo, or a company's own when one is set on
/// Settings > Appearance. It never moves: a company's logo replaces the HTW one and stays still.
///
/// A company's logo is drawn on a light plate, because the sidebar is navy in both themes and an
/// uploaded PNG is as likely to be dark lettering as light; the HTW logo is the reverse artwork made
/// for navy and needs none.
class LogoSpot extends StatelessWidget {
  const LogoSpot({super.key, required this.organisation});

  final Organisation organisation;

  /// The tile's size. Fixed, so the sidebar below it does not move when a company logo is set.
  static const size = Size(160, 72);

  @override
  Widget build(BuildContext context) {
    final logo = organisation.logoPng;

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

    return Align(
      alignment: Alignment.centerLeft,
      child: SizedBox.fromSize(size: size, child: Center(child: brand)),
    );
  }
}
