import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/di/locator.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/data/models/exposed_application_mapper.dart';
import 'package:kintsugi_web/domain/entities/exposed_application.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/vulnerability_usecases.dart';
import 'package:kintsugi_web/presentation/vulnerabilities/exposed_applications_screen.dart';

class _FakeRepository implements ExposedApplicationRepository {
  _FakeRepository(this.result);

  final Object result; // a List<ExposedApplication> or an ApiException to throw

  @override
  Future<List<ExposedApplication>> read() async {
    final value = result;
    if (value is ApiException) throw value;
    return value as List<ExposedApplication>;
  }
}

const _managed = ExposedApplication(
  applicationName: 'Ninja Remote',
  route: ExposureRoute.managedElsewhere,
  owner: 'NinjaOne',
  ownerIsAuto: true,
  criticality: ExposureCriticality.critical,
  worstCvssScore: 9.8,
  worstCveId: 'CVE-2026-1111',
  cveCount: 3,
  knownExploited: true,
  knownRansomwareUse: false,
  hostCount: 12,
);

const _orphan = ExposedApplication(
  applicationName: 'NextVoice',
  route: ExposureRoute.noUpgradePath,
  owner: null,
  ownerIsAuto: false,
  criticality: ExposureCriticality.unscored,
  worstCvssScore: null,
  worstCveId: 'CVE-2026-2222',
  cveCount: 1,
  knownExploited: false,
  knownRansomwareUse: false,
  hostCount: 2,
);

Future<void> _pump(WidgetTester tester, Object result) async {
  locator.registerSingleton(GetExposedApplications(_FakeRepository(result)));
  tester.view.physicalSize = const Size(1400, 1600);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(const MaterialApp(home: Scaffold(body: ExposedApplicationsScreen())));
  await tester.pumpAndSettle();
}

// StatusChip draws its label in capitals (the guide's badge), so chips are found by the uppercase text.
void main() {
  tearDown(() => locator.reset());

  test('the mapper reads a row, and an unknown route or criticality reads as the louder one', () {
    final rows = exposedApplicationsFromJson({
      'applications': [
        {
          'applicationName': 'Ninja Remote',
          'route': 'ManagedElsewhere',
          'owner': 'NinjaOne',
          'source': 'Auto',
          'criticality': 'High',
          'worstCvssScore': 7.5,
          'worstCveId': 'CVE-1',
          'cveCount': 2,
          'knownExploited': false,
          'knownRansomwareUse': false,
          'hostCount': 5,
        },
        {'applicationName': 'Future', 'route': 'SomethingNew', 'criticality': 'Severe'},
      ],
    });

    expect(rows.first.route, ExposureRoute.managedElsewhere);
    expect(rows.first.ownerIsAuto, isTrue);
    expect(rows.first.criticality, ExposureCriticality.high);
    expect(rows.first.worstCvssScore, 7.5);
    expect(rows.last.route, ExposureRoute.noUpgradePath);
    expect(rows.last.criticality, ExposureCriticality.critical);
  });

  testWidgets('a managed application shows its owner, its CVE and who to ask', (tester) async {
    await _pump(tester, [_managed]);

    expect(find.text('Ninja Remote'), findsOneWidget);
    expect(find.text('CRITICAL'), findsOneWidget);
    expect(find.text('EXPLOITED'), findsOneWidget);
    expect(find.text('MANAGED · NINJAONE (AUTO)'), findsOneWidget);
    expect(find.text('CVE-2026-1111 (CVSS 9.8) · 3 CVEs · 12 host(s)'), findsOneWidget);
    expect(find.textContaining('Ask NinjaOne for a fixed version'), findsOneWidget);
  });

  testWidgets('an application with no owner says there is no owner and no update path', (tester) async {
    await _pump(tester, [_orphan]);

    expect(find.text('NO OWNER · NO UPDATE PATH'), findsOneWidget);
    expect(find.text('UNSCORED'), findsOneWidget);
    expect(find.textContaining('Nobody is named'), findsOneWidget);
    expect(find.text('CVE-2026-2222 · 2 host(s)'), findsOneWidget);
  });

  testWidgets('an empty list says so', (tester) async {
    await _pump(tester, <ExposedApplication>[]);

    expect(find.textContaining('No application with an open CVE'), findsOneWidget);
  });

  testWidgets('a failed load shows the error', (tester) async {
    await _pump(tester, const ApiException('Server said no.'));

    expect(find.text('Server said no.'), findsOneWidget);
    expect(find.text('Could not load the list.'), findsOneWidget);
  });
}
