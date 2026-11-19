import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/settings.dart';
import '../../domain/usecases/settings_usecases.dart';
import 'settings_state.dart';

sealed class OutlookSignaturesEvent extends Equatable {
  const OutlookSignaturesEvent();

  @override
  List<Object?> get props => const [];
}

/// Reads the settings and the last pull's outcome. Also what "Refresh Status" sends.
final class OutlookSignaturesRequested extends OutlookSignaturesEvent {
  const OutlookSignaturesRequested();
}

final class OutlookSignaturesSaveRequested extends OutlookSignaturesEvent {
  const OutlookSignaturesSaveRequested({
    required this.enabled,
    required this.endpointUrl,
    required this.windowsUsernameSuffix,
  });

  final bool enabled;
  final String? endpointUrl;
  final String? windowsUsernameSuffix;

  @override
  List<Object?> get props => [enabled, endpointUrl, windowsUsernameSuffix];
}

final class OutlookSignaturesEdited extends OutlookSignaturesEvent {
  const OutlookSignaturesEdited();
}

/// Settings > Outlook Signatures. It does not poll: the pull runs hourly, and a poll re-hydrating the
/// form would overwrite what somebody is typing. "Refresh Status" re-reads on request instead.
class OutlookSignaturesBloc extends Bloc<OutlookSignaturesEvent, SettingsState<OutlookSignatureSource>> {
  OutlookSignaturesBloc({
    required GetOutlookSignatureSource getSource,
    required UpdateOutlookSignatureSource updateSource,
  })  : _getSource = getSource,
        _updateSource = updateSource,
        super(const SettingsState()) {
    on<OutlookSignaturesRequested>(_onRequested);
    on<OutlookSignaturesSaveRequested>(_onSave);
    on<OutlookSignaturesEdited>(
      (_, emit) => emit(state.copyWith(saved: false, clearError: true, fieldErrors: const {})),
    );
  }

  final GetOutlookSignatureSource _getSource;
  final UpdateOutlookSignatureSource _updateSource;

  Future<void> _onRequested(
    OutlookSignaturesRequested event,
    Emitter<SettingsState<OutlookSignatureSource>> emit,
  ) async {
    emit(state.copyWith(loading: true, clearError: true));
    try {
      emit(state.copyWith(value: await _getSource(), loading: false));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onSave(
    OutlookSignaturesSaveRequested event,
    Emitter<SettingsState<OutlookSignatureSource>> emit,
  ) async {
    emit(state.copyWith(saving: true, saved: false, clearError: true, fieldErrors: const {}));
    try {
      final saved = await _updateSource(
        enabled: event.enabled,
        endpointUrl: event.endpointUrl,
        windowsUsernameSuffix: event.windowsUsernameSuffix,
      );
      emit(state.copyWith(value: saved, saving: false, saved: true));
    } on ApiException catch (error) {
      emit(state.copyWith(
        saving: false,
        error: error.validationErrors.isEmpty ? error.message : null,
        fieldErrors: error.validationErrors,
      ));
    }
  }
}
