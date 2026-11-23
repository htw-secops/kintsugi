import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/painting.dart';

/// The colour an image is "about": the most vivid hue in it, so a logo's brand blue wins over the
/// navy it sits on. Null for a grey, black or white image, which has no hue to pick.
///
/// [rgba] is straight 8-bit RGBA, four bytes a pixel. Each pixel votes for its hue (24 slices of the
/// wheel) with a weight of saturation × brightness, and pixels that are dull or dark do not vote at
/// all, so a big flat background cannot outvote a small bright logo. The answer is the weighted
/// average of the winning slice.
Color? accentFromRgba(Uint8List rgba) {
  const bins = 24;
  final weight = List<double>.filled(bins, 0);
  final red = List<double>.filled(bins, 0);
  final green = List<double>.filled(bins, 0);
  final blue = List<double>.filled(bins, 0);

  for (var i = 0; i + 3 < rgba.length; i += 4) {
    if (rgba[i + 3] < 128) continue;
    final colour = Color.fromARGB(255, rgba[i], rgba[i + 1], rgba[i + 2]);
    final hsv = HSVColor.fromColor(colour);
    if (hsv.saturation < 0.35 || hsv.value < 0.35) continue;
    final bin = (hsv.hue / 360 * bins).floor() % bins;
    final w = hsv.saturation * hsv.value;
    weight[bin] += w;
    red[bin] += rgba[i] * w;
    green[bin] += rgba[i + 1] * w;
    blue[bin] += rgba[i + 2] * w;
  }

  var best = 0;
  for (var b = 1; b < bins; b++) {
    if (weight[b] > weight[best]) best = b;
  }
  if (weight[best] == 0) return null;
  return Color.fromARGB(
    255,
    (red[best] / weight[best]).round(),
    (green[best] / weight[best]).round(),
    (blue[best] / weight[best]).round(),
  );
}

/// [accentFromRgba] for an encoded PNG, JPEG or WebP. Decoded at 64 pixels wide: the answer is an
/// average, so more pixels would only cost time. Null when it has no hue or cannot be decoded.
Future<Color?> accentFromImage(Uint8List encoded) async {
  try {
    final codec = await ui.instantiateImageCodec(encoded, targetWidth: 64);
    final frame = await codec.getNextFrame();
    final data = await frame.image.toByteData(format: ui.ImageByteFormat.rawRgba);
    frame.image.dispose();
    codec.dispose();
    return data == null ? null : accentFromRgba(data.buffer.asUint8List());
  } catch (_) {
    return null;
  }
}
