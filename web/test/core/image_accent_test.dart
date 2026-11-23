import 'dart:typed_data';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/theme/image_accent.dart';
import 'package:kintsugi_web/domain/entities/appearance.dart';

/// A `size` x `size` image of [background] with a `dot` x `dot` square of [dotColour] in a corner.
Future<Uint8List> pngOf(Color background, Color dotColour, {int size = 40, int dot = 6}) async {
  final recorder = ui.PictureRecorder();
  final canvas = Canvas(recorder);
  canvas.drawRect(Rect.fromLTWH(0, 0, size.toDouble(), size.toDouble()), Paint()..color = background);
  canvas.drawRect(Rect.fromLTWH(2, 2, dot.toDouble(), dot.toDouble()), Paint()..color = dotColour);
  final image = await recorder.endRecording().toImage(size, size);
  final data = await image.toByteData(format: ui.ImageByteFormat.png);
  return data!.buffer.asUint8List();
}

Uint8List rgba(List<Color> pixels) => Uint8List.fromList([
      for (final c in pixels) ...[(c.r * 255).round(), (c.g * 255).round(), (c.b * 255).round(), 255],
    ]);

void main() {
  test('a small bright logo colour beats the big dark background around it', () {
    const navy = Color(0xFF001428);
    const brand = Color(0xFF00AEEF);
    final colour = accentFromRgba(rgba([...List.filled(500, navy), ...List.filled(20, brand)]));
    expect(colour, brand);
  });

  test('the biggest vivid area wins between two vivid hues', () {
    final colour = accentFromRgba(rgba([...List.filled(30, const Color(0xFFE53935)), ...List.filled(5, const Color(0xFF1E88E5))]));
    expect(HSVColor.fromColor(colour!).hue, lessThan(15));
  });

  test('greys, blacks, whites and transparent pixels give no colour', () {
    expect(accentFromRgba(rgba(List.filled(50, const Color(0xFF808080)))), isNull);
    expect(accentFromRgba(rgba(List.filled(50, const Color(0xFF000000)))), isNull);
    expect(accentFromRgba(rgba(List.filled(50, const Color(0xFFFFFFFF)))), isNull);
    expect(accentFromRgba(Uint8List.fromList([255, 0, 0, 0, 255, 0, 0, 10])), isNull);
  });

  testWidgets('an encoded image is decoded and its colour found', (tester) async {
    await tester.runAsync(() async {
      final png = await pngOf(const Color(0xFF001428), const Color(0xFF00AEEF));
      final colour = await accentFromImage(png);
      expect(colour, isNotNull);
      expect(HSVColor.fromColor(colour!).hue, closeTo(HSVColor.fromColor(const Color(0xFF00AEEF)).hue, 6));
      expect(await accentFromImage(Uint8List.fromList([1, 2, 3])), isNull);
    });
  });

  test('wallpaper files: PNG, JPEG and WebP up to 1 MB are accepted, anything else says why', () {
    final png = Uint8List.fromList([0x89, 0x50, 0x4E, 0x47, 0, 0]);
    final jpeg = Uint8List.fromList([0xFF, 0xD8, 0xFF, 0xE0]);
    final webp = Uint8List.fromList([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50]);
    expect(AppearanceSettings.wallpaperProblem(png), isNull);
    expect(AppearanceSettings.wallpaperProblem(jpeg), isNull);
    expect(AppearanceSettings.wallpaperProblem(webp), isNull);
    expect(AppearanceSettings.wallpaperProblem(Uint8List.fromList([0x47, 0x49, 0x46])), contains('PNG, JPEG or WebP'));
    expect(AppearanceSettings.wallpaperProblem(Uint8List.fromList([0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45])), contains('PNG, JPEG or WebP'));
    expect(AppearanceSettings.wallpaperProblem(Uint8List(AppearanceSettings.maxWallpaperBytes + 1)), AppearanceSettings.wallpaperTooLarge);
  });
}
