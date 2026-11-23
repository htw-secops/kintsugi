import 'dart:typed_data';

import 'package:equatable/equatable.dart';
import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/appearance.dart';
import '../../domain/usecases/appearance_usecases.dart';
import '../../domain/usecases/settings_usecases.dart';

class AppearanceState extends Equatable {
  const AppearanceState({
    this.saved = AppearanceSettings.defaults,
    this.preview,
    this.organisationName,
    this.organisationLogoPng,
    this.saving = false,
    this.justSaved = false,
    this.error,
  });

  /// What the server has.
  final AppearanceSettings saved;

  /// What the Appearance screen is trying out. Applied to the whole app at once, so a company sees
  /// the result before saving it. Null when nothing is being tried.
  final AppearanceSettings? preview;

  /// From the patching policy, which owns them. Shown at the top of the sidebar.
  final String? organisationName;
  final Uint8List? organisationLogoPng;

  final bool saving;
  final bool justSaved;
  final String? error;

  AppearanceSettings get effective => preview ?? saved;

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
    String? organisationName,
    Uint8List? organisationLogoPng,
    bool? saving,
    bool? justSaved,
    String? error,
    bool clearError = false,
  }) =>
      AppearanceState(
        saved: saved ?? this.saved,
        preview: clearPreview ? null : (preview ?? this.preview),
        organisationName: organisationName ?? this.organisationName,
        organisationLogoPng: organisationLogoPng ?? this.organisationLogoPng,
        saving: saving ?? this.saving,
        justSaved: justSaved ?? this.justSaved,
        error: clearError ? null : (error ?? this.error),
      );

  @override
  List<Object?> get props => [saved, preview, organisationName, organisationLogoPng, saving, justSaved, error];
}

/// The company's look, held above the router so the whole app can follow it.
class AppearanceCubit extends Cubit<AppearanceState> {
  AppearanceCubit({
    required GetAppearanceSettings getSettings,
    required UpdateAppearanceSettings updateSettings,
    required GetPatchingPolicySettings getPolicy,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        _getPolicy = getPolicy,
        super(const AppearanceState());

  final GetAppearanceSettings _getSettings;
  final UpdateAppearanceSettings _updateSettings;
  final GetPatchingPolicySettings _getPolicy;

  /// Reads the company's settings once someone is signed in. A failure is ignored on purpose: the
  /// look of the app must never be the reason it cannot be used, so the original palette stays.
  Future<void> load() async {
    try {
      emit(state.copyWith(saved: await _getSettings()));
    } on ApiException {
      // Keep the defaults.
    }
    try {
      final policy = await _getPolicy();
      emit(state.copyWith(
        organisationName: policy.organisationName,
        organisationLogoPng: policy.organisationLogoPng,
      ));
    } on ApiException {
      // No name or logo in the sidebar.
    }
  }

  void preview(AppearanceSettings settings) => emit(state.copyWith(preview: settings, justSaved: false, clearError: true));

  void discardPreview() => emit(state.copyWith(clearPreview: true));

  /// Saves [settings]. True when the server accepted them.
  ///
  /// [onAccepted] runs after the server said yes and before the app is told, so a change it makes
  /// (the browser's own colour-mode pick, dropped) is part of the same rebuild.
  Future<bool> save(AppearanceSettings settings, {Future<void> Function()? onAccepted}) async {
    emit(state.copyWith(saving: true, justSaved: false, clearError: true));
    try {
      final stored = await _updateSettings(settings);
      await onAccepted?.call();
      emit(state.copyWith(saved: stored, clearPreview: true, saving: false, justSaved: true));
      return true;
    } on ApiException catch (error) {
      emit(state.copyWith(saving: false, error: error.message));
      return false;
    }
  }
}
