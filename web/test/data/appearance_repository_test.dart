import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kintsugi_web/core/network/api_client.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/data/repositories/appearance_repository_impl.dart';
import 'package:kintsugi_web/domain/entities/appearance.dart';

/// The two halves of `/api/admin/settings/appearance` are compiled separately and nothing else checks
/// that they agree, so the same JSON text is pinned on both sides: the server's
/// `AppearanceRouteTests` binds `_sent` into `UpdateAppearanceSettingsCommand`, and
/// `GetAppearanceSettingsQueryHandlerTests` serialises its DTO to `_received`. Change a name here and
/// the matching C# test must change with it.
void main() {
  // What the C# DTO serialises to (camelCase, the wallpaper as base64: PNG signature bytes 89 50 4E 47).
  const received =
      '{"colourMode":"light","presetKey":"gold","accentHex":"#F2B01E","wallpaper":"iVBORw==","logoAnimation":"fade"}';
  final png = Uint8List.fromList([0x89, 0x50, 0x4E, 0x47]);

  AppearanceSettingsRepositoryImpl repositoryAnswering(int status, String body, {void Function(http.Request)? onRequest}) =>
      AppearanceSettingsRepositoryImpl(ApiClient(
        httpClient: MockClient((request) async {
          onRequest?.call(request);
          return http.Response(body, status, headers: {'content-type': 'application/json'});
        }),
      ));

  test('reads exactly what the server sends', () async {
    late http.Request seen;
    final settings = await repositoryAnswering(200, received, onRequest: (r) => seen = r).read();

    expect(seen.method, 'GET');
    expect(seen.url.path, '/api/admin/settings/appearance');
    expect(settings, AppearanceSettings(colourMode: 'light', presetKey: 'gold', accentHex: '#F2B01E', wallpaper: png, logoAnimation: 'fade'));
  });

  test('sends exactly what the server binds: the same field names, the picture as base64', () async {
    late http.Request seen;
    await repositoryAnswering(200, received, onRequest: (r) => seen = r).update(
      AppearanceSettings(colourMode: 'light', presetKey: 'gold', accentHex: '#F2B01E', wallpaper: png, logoAnimation: 'fade'),
    );

    expect(seen.method, 'PUT');
    expect(seen.url.path, '/api/admin/settings/appearance');
    expect(jsonDecode(seen.body), {
      'colourMode': 'light',
      'presetKey': 'gold',
      'accentHex': '#F2B01E',
      'wallpaper': 'iVBORw==',
      'logoAnimation': 'fade',
    });
  });

  test('no colour and no picture are sent as null, which the server reads as "none"', () async {
    late http.Request seen;
    await repositoryAnswering(200, received, onRequest: (r) => seen = r).update(AppearanceSettings.defaults);

    final body = jsonDecode(seen.body) as Map<String, dynamic>;
    expect(body['accentHex'], isNull);
    expect(body['wallpaper'], isNull);
    expect(body.keys, containsAll(['colourMode', 'presetKey', 'accentHex', 'wallpaper', 'logoAnimation']));
  });

  test('a server that omits the newer fields reads as the defaults', () async {
    final settings = await repositoryAnswering(200, '{}').read();

    expect(settings, AppearanceSettings.defaults);
  });

  test('an empty wallpaper string is no wallpaper', () async {
    final settings = await repositoryAnswering(200, '{"wallpaper":""}').read();

    expect(settings.wallpaper, isNull);
  });

  test('a server without the route answers 404, which the app reads as "not supported yet"', () async {
    await expectLater(
      repositoryAnswering(404, '{"title":"Not Found"}').read(),
      throwsA(isA<ApiException>().having((e) => e.statusCode, 'statusCode', 404)),
    );
  });

  test('a refused value arrives as a field error under the property name the server used', () async {
    const problem = '{"title":"One or more validation errors occurred.","errors":{"AccentHex":["The accent colour must be six hex digits, for example #7C3AED."]}}';

    await expectLater(
      repositoryAnswering(400, problem).update(AppearanceSettings.defaults),
      throwsA(isA<ApiException>().having((e) => e.validationErrors['AccentHex'], 'AccentHex', isNotEmpty)),
    );
  });
}
