// The admin UI's HTW style is htw-style's, and these two tests are what hold it there.
//
// Flutter cannot load CSS, so `KintsugiPalette` mirrors the tokens of the htw.css vendored into
// web/htw/ (by htw-style's scripts/sync-to-app.sh; never served, never edited). The first test is
// htw-style's own vendored-file check (STYLE-GUIDE.md, "The vendored check"), ported from its
// pytest snippet: an edited copy, or one at a different version from .htw-style-version, fails.
// The second fails if any palette field differs from the token it names, so a re-sync that moves
// a colour fails CI until the palette follows. `flutter test` runs from web/.
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/kintsugi_palette.dart';

final _header = RegExp(r'^/\* htw-style v(\d+\.\d+\.\d+) · sha256 ([0-9a-f]{64}) · '
    r'DO NOT EDIT: change HTW-Infra/htw-style and re-sync \*/\n');

String _vendored() => File('htw/htw.css').readAsStringSync().replaceAll('\r\n', '\n');

/// htw.css's `:root` custom properties, `var()` followed. The first `:root` block holds them all.
Map<String, String> _rootTokens() {
  final css = _vendored().replaceAll(RegExp(r'/\*.*?\*/', dotAll: true), '');
  final body = RegExp(r':root\s*\{([^}]*)\}').firstMatch(css)!.group(1)!;
  final raw = {
    for (final m in RegExp(r'(--[\w-]+)\s*:\s*([^;]+);').allMatches(body)) m.group(1)!: m.group(2)!.trim(),
  };
  String resolve(String v) {
    final ref = RegExp(r'^var\((--[\w-]+)\)$').firstMatch(v);
    return ref == null ? v : resolve(raw[ref.group(1)!]!);
  }

  return {for (final e in raw.entries) e.key: resolve(e.value)};
}

/// A CSS colour (#rgb, #rrggbb, rgba()) or the colour of a one-layer box-shadow, as ARGB.
int _argb(String css) {
  css = css.trim();
  if (css == 'white' || css == '#fff') return 0xFFFFFFFF;
  final rgba = RegExp(r'rgba\((\d+),\s*(\d+),\s*(\d+),\s*([\d.]+)\)').firstMatch(css);
  if (rgba != null) {
    final a = (double.parse(rgba.group(4)!) * 255).round();
    final [r, g, b] = [1, 2, 3].map((i) => int.parse(rgba.group(i)!)).toList();
    return a << 24 | r << 16 | g << 8 | b;
  }
  final hex = RegExp(r'^#([0-9a-fA-F]{6})$').firstMatch(css);
  expect(hex, isNotNull, reason: 'not a colour: $css');
  return 0xFF000000 | int.parse(hex!.group(1)!, radix: 16);
}

/// Field → token, for each theme. `white` stands for a literal the CSS writes as `#fff`.
const _light = {
  'background': '--htw-neutral-50', 'surface': 'white', 'subtle': '--htw-neutral-100',
  'nav': '--htw-navy', 'navLine': '--htw-logo-cyan', 'onNav': 'white', 'navMuted': '--htw-cyan-100',
  'navPill': 'white', 'navPillText': '--htw-action-text', 'text': '--htw-ink', 'muted': '--htw-slate',
  'border': '--htw-neutral-200', 'inputBorder': '--htw-input-border', 'action': '--htw-action',
  'actionHover': '--htw-action-hover', 'onAction': '--htw-action-on', 'actionTint': '--htw-action-tint',
  'actionTintText': '--htw-action-tint-text', 'focus': '--htw-cyan-mid', 'green': '--htw-success-text',
  'greenTint': '--htw-success-tint', 'amber': '--htw-warning-text', 'amberTint': '--htw-warning-tint',
  'red': '--htw-destructive-text', 'redTint': '--htw-destructive-tint', 'danger': '--htw-danger',
  'shadow': '--shadow-1',
};

const _dark = {
  'background': '--htw-dark-page', 'surface': '--htw-dark-surface', 'subtle': '--htw-dark-subtle',
  'nav': '--htw-dark-nav', 'navLine': '--htw-logo-cyan', 'onNav': '--htw-dark-ink',
  'navMuted': '--htw-cyan-100', 'navPill': '--htw-dark-action-tint',
  'navPillText': '--htw-dark-action-tint-text', 'text': '--htw-dark-ink', 'muted': '--htw-dark-slate',
  'border': '--htw-dark-hairline', 'inputBorder': '--htw-dark-input-border', 'action': '--htw-dark-action',
  'actionHover': '--htw-dark-action-hover', 'onAction': '--htw-dark-action-on',
  'actionTint': '--htw-dark-action-tint', 'actionTintText': '--htw-dark-action-tint-text',
  'focus': '--htw-dark-focus', 'green': '--htw-dark-success-text', 'greenTint': '--htw-dark-success-tint',
  'amber': '--htw-dark-warning-text', 'amberTint': '--htw-dark-warning-tint',
  'red': '--htw-dark-destructive-text', 'redTint': '--htw-dark-destructive-tint', 'danger': '--htw-danger',
  'shadow': '--htw-dark-shadow-1',
};

Map<String, int> _fields(KintsugiPalette p) => {
      'background': p.background, 'surface': p.surface, 'subtle': p.subtle, 'nav': p.nav,
      'navLine': p.navLine, 'onNav': p.onNav, 'navMuted': p.navMuted, 'navPill': p.navPill,
      'navPillText': p.navPillText, 'text': p.text, 'muted': p.muted, 'border': p.border,
      'inputBorder': p.inputBorder, 'action': p.action, 'actionHover': p.actionHover,
      'onAction': p.onAction, 'actionTint': p.actionTint, 'actionTintText': p.actionTintText,
      'focus': p.focus, 'green': p.green, 'greenTint': p.greenTint, 'amber': p.amber,
      'amberTint': p.amberTint, 'red': p.red, 'redTint': p.redTint, 'danger': p.danger,
      'shadow': p.shadow.single.color,
    }.map((k, Color c) => MapEntry(k, c.toARGB32()));

void main() {
  test('the vendored htw.css is unedited and the version .htw-style-version names', () {
    final text = _vendored();
    final m = _header.firstMatch(text);
    expect(m, isNotNull, reason: 'web/htw/htw.css has no htw-style header; re-sync it, do not edit it');
    expect(sha256.convert(utf8.encode(text.substring(m!.end))).toString(), m.group(2),
        reason: 'web/htw/htw.css was edited after sync; change HTW-Infra/htw-style and re-sync');
    expect(m.group(1), File('.htw-style-version').readAsStringSync().trim());
  });

  for (final (name, palette, map) in [
    ('light', KintsugiPalette.light, _light),
    ('dark', KintsugiPalette.dark, _dark),
  ]) {
    test('every $name palette colour is the htw.css token it names', () {
      final tokens = _rootTokens();
      final fields = _fields(palette);
      expect(map.keys.toSet(), fields.keys.toSet(), reason: 'a palette field has no token, or the reverse');
      for (final MapEntry(key: field, value: token) in map.entries) {
        final css = token == 'white' ? 'white' : tokens[token];
        expect(css, isNotNull, reason: '$token is not in the vendored htw.css');
        // A shadow token is "x y blur colour"; the colour is its last part.
        final colour = token.contains('shadow') ? RegExp(r'rgba\([^)]*\)').firstMatch(css!)!.group(0)! : css!;
        expect(fields[field]!.toRadixString(16), _argb(colour).toRadixString(16),
            reason: '$name `$field` is not $token ($css)');
      }
      expect([for (final c in palette.chart) c.toARGB32()], [for (var i = 1; i <= 5; i++) _argb(tokens['--chart-$i']!)],
          reason: '$name `chart` is not --chart-1..5');
    });
  }
}
