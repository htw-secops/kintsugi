import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

/// Settings > Device Investigation: the shared settings shape, plus how the last test message went.
class DeviceInvestigationState extends Equatable {
  const DeviceInvestigationState({
    this.settings = const SettingsState<DeviceInvestigationSettings>(),
    this.testing = false,
    this.testSent = false,
    this.testError,
  });

  final SettingsState<DeviceInvestigationSettings> settings;
  final bool testing;
  final bool testSent;

  /// The server's reason the webhook did not take the test message. Kept apart from the settings error so
  /// a refused test does not read as a failed save.
  final String? testError;

  DeviceInvestigationState copyWith({
    SettingsState<DeviceInvestigationSettings>? settings,
    bool? testing,
    bool? testSent,
    String? testError,
    bool clearTestError = false,
  }) =>
      DeviceInvestigationState(
        settings: settings ?? this.settings,
        testing: testing ?? this.testing,
        testSent: testSent ?? this.testSent,
        testError: clearTestError ? null : (testError ?? this.testError),
      );

  @override
  List<Object?> get props => [settings, testing, testSent, testError];
}

/// A cubit rather than a bloc so the webhook travels as a method argument and never sits in an event's
/// props, where a `BlocObserver` or a test failure would print it.
class DeviceInvestigationCubit extends Cubit<DeviceInvestigationState> {
  DeviceInvestigationCubit({
    required GetDeviceInvestigationSettings getSettings,
    required UpdateDeviceInvestigationSettings updateSettings,
    required SendDeviceInvestigationTestAlert sendTest,
  })  : _getSettings = getSettings,
        _updateSettings = updateSettings,
        _sendTest = sendTest,
        super(const DeviceInvestigationState());

  final GetDeviceInvestigationSettings _getSettings;
  final UpdateDeviceInvestigationSettings _updateSettings;
  final SendDeviceInvestigationTestAlert _sendTest;

  Future<void> load() async {
    emit(state.copyWith(settings: state.settings.copyWith(loading: true, clearError: true)));
    try {
      final settings = await _getSettings();
      emit(state.copyWith(settings: state.settings.copyWith(value: settings, loading: false)));
    } on ApiException catch (error) {
      emit(state.copyWith(settings: state.settings.copyWith(loading: false, error: error.message)));
    }
  }

  /// A blank [slackWebhookUrl] keeps the stored webhook; [clearSlackWebhook] removes it.
  Future<void> save({
    required bool enabled,
    required String? slackWebhookUrl,
    required String? consoleBaseUrl,
    bool clearSlackWebhook = false,
  }) async {
    emit(state.copyWith(
      settings: state.settings.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}),
      testSent: false,
      clearTestError: true,
    ));
    try {
      final saved = await _updateSettings(
        enabled: enabled,
        slackWebhookUrl: slackWebhookUrl,
        consoleBaseUrl: consoleBaseUrl,
        clearSlackWebhook: clearSlackWebhook,
      );
      emit(state.copyWith(settings: state.settings.copyWith(value: saved, saving: false, saved: true)));
    } on ApiException catch (error) {
      emit(state.copyWith(
        settings: state.settings.copyWith(
          saving: false,
          error: error.validationErrors.isEmpty ? error.message : null,
          fieldErrors: error.validationErrors,
        ),
      ));
    }
  }

  void edited() => emit(state.copyWith(settings: state.settings.copyWith(saved: false, clearError: true, fieldErrors: const {})));

  Future<void> sendTest() async {
    emit(state.copyWith(testing: true, testSent: false, clearTestError: true));
    try {
      await _sendTest();
      emit(state.copyWith(testing: false, testSent: true));
    } on ApiException catch (error) {
      emit(state.copyWith(testing: false, testError: error.message));
    }
  }
}
