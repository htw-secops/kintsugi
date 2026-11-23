import 'package:flutter/material.dart';

import '../../../core/theme/app_theme.dart';

/// The picture beside an application's name: its logo when one ships with the app, otherwise a
/// coloured tile with its first letter.
///
/// **Logos are bundled, never fetched.** An icon loaded from a third-party service would tell that
/// service which applications this fleet runs, every time an administrator opened the screen, and
/// the agents do not report icons. So the set in `assets/img/apps/` is small and fixed (see its
/// README), and everything outside it gets the letter tile. Add an application by adding a PNG and
/// one line to [_logos].
class AppIcon extends StatelessWidget {
  const AppIcon(this.name, {super.key, this.size = 28});

  final String name;
  final double size;

  /// Name pattern → file in `assets/img/apps/`. Whole words, so "slack" does not match
  /// "Slackware" and "zoom" does not match "ZoomText": the same trap `CpeConfidence` documents.
  static final _logos = <(RegExp, String)>[
    (RegExp(r'\bchrome\b', caseSensitive: false), 'googlechrome'),
    (RegExp(r'\bfirefox\b', caseSensitive: false), 'firefox'),
    (RegExp(r'\bslack\b', caseSensitive: false), 'slack'),
    (RegExp(r'\bzoom\b', caseSensitive: false), 'zoom'),
    (RegExp(r'\bdocker\b', caseSensitive: false), 'docker'),
    (RegExp(r'^git( for windows)?$', caseSensitive: false), 'git'),
    (RegExp(r'\b7-?zip\b', caseSensitive: false), '7zip'),
    (RegExp(r'notepad\+\+', caseSensitive: false), 'notepadplusplus'),
    (RegExp(r'\bvlc\b', caseSensitive: false), 'vlcmediaplayer'),
    (RegExp(r'\bpython\b', caseSensitive: false), 'python'),
    (RegExp(r'\bnode(\.js)?\b', caseSensitive: false), 'nodedotjs'),
    (RegExp(r'\bopenssl\b', caseSensitive: false), 'openssl'),
    (RegExp(r'\bhomebrew\b', caseSensitive: false), 'homebrew'),
    (RegExp(r'\bwireshark\b', caseSensitive: false), 'wireshark'),
    (RegExp(r'\bgimp\b', caseSensitive: false), 'gimp'),
    (RegExp(r'\blibreoffice\b', caseSensitive: false), 'libreoffice'),
    (RegExp(r'\bthunderbird\b', caseSensitive: false), 'thunderbird'),
    (RegExp(r'\bdropbox\b', caseSensitive: false), 'dropbox'),
    (RegExp(r'\bspotify\b', caseSensitive: false), 'spotify'),
    (RegExp(r'\b1password\b', caseSensitive: false), '1password'),
    (RegExp(r'\bbitwarden\b', caseSensitive: false), 'bitwarden'),
    (RegExp(r'\bpostman\b', caseSensitive: false), 'postman'),
    (RegExp(r'\bsublime text\b', caseSensitive: false), 'sublimetext'),
    (RegExp(r'\bacrobat\b', caseSensitive: false), 'adobeacrobatreader'),
    (RegExp(r'^curl$', caseSensitive: false), 'curl'),
  ];

  /// The bundled logo for [name], or null when there is none.
  static String? assetFor(String name) {
    for (final (pattern, file) in _logos) {
      if (pattern.hasMatch(name)) return 'assets/img/apps/$file.png';
    }
    return null;
  }

  @override
  Widget build(BuildContext context) {
    final asset = assetFor(name);
    // Decorative: the name is printed beside it, so a screen reader should not say it twice.
    return ExcludeSemantics(
      child: SizedBox(
        width: size,
        height: size,
        child: asset == null
            ? _LetterTile(name, size)
            : Image.asset(asset, width: size, height: size, errorBuilder: (_, _, _) => _LetterTile(name, size)),
      ),
    );
  }
}

class _LetterTile extends StatelessWidget {
  const _LetterTile(this.name, this.size);

  final String name;
  final double size;

  @override
  Widget build(BuildContext context) {
    final letter = RegExp(r'[A-Za-z0-9]').firstMatch(name)?.group(0)?.toUpperCase() ?? '?';
    // A stable hue from the name, so an application keeps its colour between visits. Dark enough
    // for white text in both themes.
    final hue = (name.toLowerCase().codeUnits.fold<int>(7, (a, c) => (a * 31 + c) & 0xFFFFFF)) % 360;
    return Container(
      alignment: Alignment.center,
      decoration: BoxDecoration(
        color: HSLColor.fromAHSL(1, hue.toDouble(), 0.45, 0.32).toColor(),
        borderRadius: BorderRadius.circular(size * 0.22),
      ),
      child: Text(letter, style: AppTheme.display(color: Colors.white, size: size * 0.46, letterSpacing: 0)),
    );
  }
}
