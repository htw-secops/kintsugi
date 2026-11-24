import 'dart:typed_data';

import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/domain/entities/appearance.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/appearance_usecases.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/appearance_cubit.dart';

class FakeAppearanceRepository implements AppearanceSettingsRepository {
  FakeAppearanceRepository([this.stored = AppearanceSettings.defaults]);

  AppearanceSettings stored;
  final saves = <AppearanceSettings>[];

  /// Set to make the server answer like one without the route: `ApiException('', statusCode: 404)`.
  ApiException? readError;

  @override
  Future<AppearanceSettings> read() async => readError == null ? stored : throw readError!;

  @override
  Future<AppearanceSettings> update(AppearanceSettings settings) async {
    saves.add(settings);
    return stored = settings;
  }
}

class FakePolicyRepository implements PatchingPolicySettingsRepository {
  FakePolicyRepository({this.name, this.logo});

  String? name;
  Uint8List? logo;
  final saves = <PatchingPolicySettings>[];

  @override
  Future<PatchingPolicySettings> read() async => PatchingPolicySettings(
        intervalValue: 7,
        intervalUnit: PatchingTimeUnit.days,
        delayValue: 1,
        delayUnit: PatchingTimeUnit.hours,
        maxDelayCount: 3,
        organisationName: name,
        organisationLogoPng: logo,
      );

  @override
  Future<PatchingPolicySettings> update(PatchingPolicySettings settings) async {
    saves.add(settings);
    name = settings.organisationName;
    logo = settings.organisationLogoPng;
    return settings;
  }
}

AppearanceCubit fakeAppearanceCubit({
  FakeAppearanceRepository? repository,
  FakePolicyRepository? policy,
  String? organisationName,
}) {
  final appearance = repository ?? FakeAppearanceRepository();
  final policies = policy ?? FakePolicyRepository(name: organisationName);
  return AppearanceCubit(
    getSettings: GetAppearanceSettings(appearance),
    updateSettings: UpdateAppearanceSettings(appearance),
    getPolicy: GetPatchingPolicySettings(policies),
    updatePolicy: UpdatePatchingPolicySettings(policies),
  );
}
