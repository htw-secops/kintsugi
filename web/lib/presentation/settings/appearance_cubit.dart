import 'dart:typed_data';

import 'package:equatable/equatable.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/appearance.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/appearance_usecases.dart';
import '../../domain/usecases/settings_usecases.dart';

/// The company's name and logo. Stored on the patching policy (the agent's pop-ups use them too);
/// this page reads and writes them there rather than keeping a second copy.
class Organisation extends Equatable {
  const Organisation({this.name, this.logoPng});

  static const none = Organisation();

  final String? name;
  final Uint8List? logoPng;

  bool get isEmpty => (name == null || name!.isEmpty) && logoPng == null;

  @override
  List<Object?> get props => [name, logoPng];
}

class AppearanceState extends Equatable {
  const AppearanceState({
    this.saved = AppearanceSettings.defaults,
    this.preview,
    this.organisation = Organisation.none,
    this.organisationPreview,
    this.unsupported = false,
    this.saving = false,
    this.justSaved = false,
    this.error,
  });

  /// What the server has.
  final AppearanceSettings saved;

  /// What the Appearance screen is trying out. Applied to the whole app at once, so a company sees
  /// the result before saving it. Null when nothing is being tried.
  final AppearanceSettings? preview;

  /// Name and logo, as stored, and as being tried on the Appearance screen.
  final Organisation organisation;
  final Organisation? organisationPreview;

  /// The server has no appearance route yet (it is older than this screen). The screen can still be
  /// tried but its colour and image cannot be saved, and says so, so this can be merged ahead of the
  /// server half. The name and logo live on the patching policy, which every server has.
  final bool unsupported;

  final bool saving;
  final bool justSaved;
  final String? error;

  AppearanceSettings get effective => preview ?? saved;
  Organisation get effectiveOrganisation => organisationPreview ?? organisation;

  /// The theme mode to use. A sun/moon choice made in this browser wins over the company's default,
  /// except while previewing, when the mode being tried must be what is on screen.
  ThemeMode modeFor({required bool browserChose, required ThemeMode browserMode}) {
    if (preview == null && browserChose) return browserMode;
    return switch (effective.colourMode) {
      'light' => ThemeMode.light,
      'system' => ThemeMode.system,
      _ => ThemeMode.dark,
    };
  }

  AppearanceState copyWith({
    AppearanceSettings? saved,
    AppearanceSettings? preview,
    bool clearPreview = false,
    Organisation? organisation,
    Organisation? organisationPreview,
    bool clearOrganisationPreview = false,
    bool? unsupported,
    bool? saving,
    bool? justSaved,
    String? error,
    bool clearError = false,
  }) =>
      AppearanceState(
        saved: saved ?? this.saved,
        preview: clearPreview ? null : (preview ?? this.preview),
        organisation: organisation ?? this.organisation,
        organisationPreview: clearOrganisationPreview ? null : (organisationPreview ?? this.organisationPreview),
        unsupported: unsupported ?? this.unsupported,
        saving: saving ?? this.saving,
        justSaved: justSaved ?? this.justSaved,
        error: clearError ? null : (error ?? this.error),
      );

  @override
  List<Object?> get props => [saved, preview, organisation, organisationPreview, unsupported, saving, justSaved, error];
}

/// The company's look, held above the router so the whole app can follow it.
class AppearanceCubit extends Cubit<AppearanceState> {
  AppearanceCubit({
    required GetAppearanceSettings getSettings,
    required UpdateAppearanceSettings updateSettings,
    required GetPatchingPolicySettings getPolicy,
    required UpdatePatchingPolicySettings updatePolicy,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        _getPolicy = getPolicy,
        _updatePolicy = updatePolicy,
        super(const AppearanceState());

  final GetAppearanceSettings _getSettings;
  final UpdateAppearanceSettings _updateSettings;
  final GetPatchingPolicySettings _getPolicy;
  final UpdatePatchingPolicySettings _updatePolicy;

  /// Reads the company's settings once someone is signed in. A failure is ignored on purpose: the
  /// look of the app must never be the reason it cannot be used, so the original palette stays.
  Future<void> load() async {
    try {
      emit(state.copyWith(saved: await _getSettings(), unsupported: false));
    } on ApiException catch (error) {
      // Keep the defaults. A route the server does not have answers 404 (or 405), and a proxy page
      // standing in for it answers 200 with something that is not JSON; either means "not yet",
      // as opposed to a failure that might pass.
      final code = error.statusCode;
      if (code == 404 || code == 405 || code == 200) emit(state.copyWith(unsupported: true));
    }
    try {
      final policy = await _getPolicy();
      emit(state.copyWith(organisation: Organisation(name: policy.organisationName, logoPng: policy.organisationLogoPng)));
    } on ApiException {
      // No company logo or name.
    }
  }

  void preview(AppearanceSettings settings) => emit(state.copyWith(preview: settings, justSaved: false, clearError: true));

  void previewOrganisation(Organisation organisation) =>
      emit(state.copyWith(organisationPreview: organisation, justSaved: false, clearError: true));

  void discardPreview() => emit(state.copyWith(clearPreview: true, clearOrganisationPreview: true));

  /// Saves what changed: the colour, image and animation on the appearance route, the name and logo
  /// on the patching policy. True when everything that had to be saved was.
  ///
  /// The patching policy is read again and written back with only the name and logo replaced, since
  /// its route takes the whole policy; a policy edit made in the same instant elsewhere would be
  /// overwritten, the same as two people saving that page.
  ///
  /// [onAccepted] runs after the server said yes and before the app is told, so a change it makes
  /// (the browser's own colour-mode pick, dropped) is part of the same rebuild.
  Future<bool> save(AppearanceSettings settings, Organisation organisation, {Future<void> Function()? onAccepted}) async {
    emit(state.copyWith(saving: true, justSaved: false, clearError: true));
    try {
      var stored = state.saved;
      if (!state.unsupported && settings != state.saved) stored = await _updateSettings(settings);
      var org = state.organisation;
      if (organisation != state.organisation) {
        final policy = await _getPolicy();
        final name = organisation.name?.trim();
        final updated = await _updatePolicy(PatchingPolicySettings(
          intervalValue: policy.intervalValue,
          intervalUnit: policy.intervalUnit,
          delayValue: policy.delayValue,
          delayUnit: policy.delayUnit,
          maxDelayCount: policy.maxDelayCount,
          organisationName: name == null || name.isEmpty ? null : name,
          organisationLogoPng: organisation.logoPng,
        ));
        org = Organisation(name: updated.organisationName, logoPng: updated.organisationLogoPng);
      }
      await onAccepted?.call();
      emit(state.copyWith(
        saved: stored,
        organisation: org,
        clearPreview: true,
        clearOrganisationPreview: true,
        saving: false,
        justSaved: true,
      ));
      return true;
    } on ApiException catch (error) {
      emit(state.copyWith(saving: false, error: error.message));
      return false;
    }
  }
}
