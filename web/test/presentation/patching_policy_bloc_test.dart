import 'dart:typed_data';

import 'package:bloc_test/bloc_test.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:kintsugi_web/core/network/api_exception.dart';
import 'package:kintsugi_web/domain/entities/enums.dart';
import 'package:kintsugi_web/domain/entities/settings.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';
import 'package:kintsugi_web/domain/usecases/settings_usecases.dart';
import 'package:kintsugi_web/presentation/settings/patching_policy_bloc.dart';
import 'package:kintsugi_web/presentation/settings/settings_state.dart';

final _logo = Uint8List.fromList([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

PatchingPolicySettings _policy({String? name, Uint8List? logo}) => PatchingPolicySettings(
      intervalValue: 7,
      intervalUnit: PatchingTimeUnit.days,
      delayValue: 1,
      delayUnit: PatchingTimeUnit.days,
      maxDelayCount: 3,
      organisationName: name,
      organisationLogoPng: logo,
    );

class _FakePatchingPolicySettingsRepository implements PatchingPolicySettingsRepository {
  _FakePatchingPolicySettingsRepository(this.stored);

  PatchingPolicySettings stored;
  PatchingPolicySettings? lastUpdate;
  ApiException? updateFailure;

  @override
  Future<PatchingPolicySettings> read() async => stored;

  @override
  Future<PatchingPolicySettings> update(PatchingPolicySettings settings) async {
    lastUpdate = settings;
    if (updateFailure != null) throw updateFailure!;
    return stored = settings;
  }
}

PatchingPolicyBloc _bloc(_FakePatchingPolicySettingsRepository repository) => PatchingPolicyBloc(
      getSettings: GetPatchingPolicySettings(repository),
      updateSettings: UpdatePatchingPolicySettings(repository),
    );

void main() {
  late _FakePatchingPolicySettingsRepository repository;

  blocTest<PatchingPolicyBloc, SettingsState<PatchingPolicySettings>>(
    'loads the branding the server holds, so the form can show it',
    build: () {
      repository = _FakePatchingPolicySettingsRepository(_policy(name: 'Example Corp IT', logo: _logo));
      return _bloc(repository);
    },
    act: (bloc) => bloc.add(const PatchingPolicyRequested()),
    verify: (bloc) {
      expect(bloc.state.loading, isFalse);
      expect(bloc.state.value?.organisationName, 'Example Corp IT');
      expect(bloc.state.value?.organisationLogoPng, _logo);
    },
  );

  blocTest<PatchingPolicyBloc, SettingsState<PatchingPolicySettings>>(
    'saves the branding with the rest of the policy and shows what the server kept',
    build: () {
      repository = _FakePatchingPolicySettingsRepository(_policy());
      return _bloc(repository);
    },
    seed: () => SettingsState<PatchingPolicySettings>(value: _policy(), loading: false),
    act: (bloc) => bloc.add(PatchingPolicySaveRequested(_policy(name: 'Example Corp IT', logo: _logo))),
    expect: () => [
      isA<SettingsState<PatchingPolicySettings>>().having((s) => s.saving, 'saving', isTrue),
      isA<SettingsState<PatchingPolicySettings>>()
          .having((s) => s.saving, 'saving', isFalse)
          .having((s) => s.saved, 'saved', isTrue)
          .having((s) => s.value?.organisationName, 'organisationName', 'Example Corp IT')
          .having((s) => s.value?.organisationLogoPng, 'organisationLogoPng', _logo),
    ],
    verify: (_) => expect(repository.lastUpdate?.organisationLogoPng, _logo),
  );

  blocTest<PatchingPolicyBloc, SettingsState<PatchingPolicySettings>>(
    'a refused logo comes back as a field error for the picker, not as a page error',
    build: () {
      repository = _FakePatchingPolicySettingsRepository(_policy())
        ..updateFailure = const ApiException(
          'One or more validation errors occurred.',
          statusCode: 400,
          validationErrors: {
            'OrganisationLogoPng': ['Organisation logo must be a PNG image.'],
          },
        );
      return _bloc(repository);
    },
    seed: () => SettingsState<PatchingPolicySettings>(value: _policy(), loading: false),
    act: (bloc) => bloc.add(PatchingPolicySaveRequested(_policy(logo: _logo))),
    verify: (bloc) {
      expect(bloc.state.saving, isFalse);
      expect(bloc.state.saved, isFalse);
      expect(bloc.state.error, isNull);
      // The key the screen looks up — C#'s property name, matched case-insensitively.
      expect(bloc.state.errorFor('OrganisationLogoPng'), 'Organisation logo must be a PNG image.');
    },
  );
}
