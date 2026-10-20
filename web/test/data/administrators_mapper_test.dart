import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/data/models/session_mapper.dart';
import 'package:kintsugi_web/data/models/settings_mapper.dart';

/// What the administrators allowlist adds to the two DTOs it travels in.
void main() {
  group('sessionFromJson authorized', () {
    test('reads what the server said', () {
      expect(sessionFromJson({'signedIn': true, 'authorized': false}).authorized, isFalse);
      expect(sessionFromJson({'signedIn': true, 'authorized': true}).authorized, isTrue);
    });

    test('is true when the server predates the field, so a new UI cannot deny everyone', () {
      expect(sessionFromJson({'signedIn': true}).authorized, isTrue);
    });
  });

  group('authenticationSettingsFromJson adminEmails', () {
    test('reads the list', () {
      final settings = authenticationSettingsFromJson({
        'provider': 0,
        'adminEmails': ['a@example.com', 'b@example.com'],
      });

      expect(settings.adminEmails, ['a@example.com', 'b@example.com']);
    });

    test('is empty when the field is absent or null', () {
      expect(authenticationSettingsFromJson({'provider': 0}).adminEmails, isEmpty);
      expect(
        authenticationSettingsFromJson({'provider': 0, 'adminEmails': null}).adminEmails,
        isEmpty,
      );
    });
  });
}
