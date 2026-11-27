import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:kintsugi_web/core/network/api_client.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/data/models/settings_mapper.dart';
import 'package:kintsugi_web/data/repositories/settings_repository_impl.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';

http.Response _json(Object body, [int status = 200]) =>
    http.Response(jsonEncode(body), status, headers: {'content-type': 'application/json'});

void main() {
  test('the mapper reads the DTO, and an empty one is off with nothing saved', () {
    expect(
      deviceInvestigationSettingsFromJson({'enabled': true, 'hasSlackWebhook': true, 'consoleBaseUrl': 'https://k.example.com'}),
      const DeviceInvestigationSettings(enabled: true, hasSlackWebhook: true, consoleBaseUrl: 'https://k.example.com'),
    );
    expect(
      deviceInvestigationSettingsFromJson(const {}),
      const DeviceInvestigationSettings(enabled: false, hasSlackWebhook: false, consoleBaseUrl: null),
    );
  });

  test('the repository round-trips: reads, writes the command shape, and reads the answer back', () async {
    final seen = <String>[];
    final bodies = <Object?>[];
    final repo = DeviceInvestigationSettingsRepositoryImpl(ApiClient(
      httpClient: MockClient((request) async {
        seen.add('${request.method} ${request.url.path}');
        bodies.add(request.body.isEmpty ? null : jsonDecode(request.body));
        if (request.method == 'GET') return _json({'enabled': false, 'hasSlackWebhook': false, 'consoleBaseUrl': null});
        if (request.method == 'PUT') {
          final body = jsonDecode(request.body) as Map<String, dynamic>;
          return _json({'enabled': body['enabled'], 'hasSlackWebhook': !(body['clearSlackWebhook'] as bool), 'consoleBaseUrl': body['consoleBaseUrl']});
        }
        return http.Response('', 204);
      }),
    ));

    expect((await repo.read()).hasSlackWebhook, isFalse);
    final saved = await repo.update(
      enabled: true,
      slackWebhookUrl: 'https://hooks.slack.com/services/T/B/x',
      consoleBaseUrl: 'https://k.example.com',
      clearSlackWebhook: false,
    );
    await repo.sendTestAlert();

    expect(saved, const DeviceInvestigationSettings(enabled: true, hasSlackWebhook: true, consoleBaseUrl: 'https://k.example.com'));
    expect(seen, [
      'GET /api/admin/settings/device-investigation',
      'PUT /api/admin/settings/device-investigation',
      'POST /api/admin/settings/device-investigation/test',
    ]);
    // `UpdateDeviceInvestigationSettingsCommand`'s four fields, by their camel-case names.
    expect(bodies[1], {
      'enabled': true,
      'slackWebhookUrl': 'https://hooks.slack.com/services/T/B/x',
      'consoleBaseUrl': 'https://k.example.com',
      'clearSlackWebhook': false,
    });
  });

  test("a webhook that refuses the test message raises the server's reason", () async {
    final repo = DeviceInvestigationSettingsRepositoryImpl(ApiClient(
      httpClient: MockClient((_) async => _json({'status': 502, 'detail': 'The webhook did not accept the message: 404 no_team'}, 502)),
    ));

    await expectLater(
      repo.sendTestAlert(),
      throwsA(isA<ApiException>().having((e) => e.message, 'message', 'The webhook did not accept the message: 404 no_team')),
    );
  });
}
