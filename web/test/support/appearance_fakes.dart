import 'dart:typed_data';

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

  @override
  Future<AppearanceSettings> read() async => stored;

  @override
  Future<AppearanceSettings> update(AppearanceSettings settings) async {
    saves.add(settings);
    return stored = settings;
  }
}

class FakePolicyRepository implements PatchingPolicySettingsRepository {
  FakePolicyRepository({this.name, this.logo});

  final String? name;
  final Uint8List? logo;

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
  Future<PatchingPolicySettings> update(PatchingPolicySettings settings) async => settings;
}

AppearanceCubit fakeAppearanceCubit({FakeAppearanceRepository? repository, String? organisationName}) {
  final appearance = repository ?? FakeAppearanceRepository();
  return AppearanceCubit(
    getSettings: GetAppearanceSettings(appearance),
    updateSettings: UpdateAppearanceSettings(appearance),
    getPolicy: GetPatchingPolicySettings(FakePolicyRepository(name: organisationName)),
  );
}
