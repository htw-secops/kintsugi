import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/network/api_exception.dart';
import '../../domain/entities/application.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/software_title.dart';
import '../../domain/usecases/software_usecases.dart';

sealed class SoftwareEvent extends Equatable {
  const SoftwareEvent();

  @override
  List<Object?> get props => const [];
}

final class SoftwareRequested extends SoftwareEvent {
  const SoftwareRequested({this.showSpinner = true});

  /// False for the Reload button and the panel's own refresh: the table is already on screen.
  final bool showSpinner;

  @override
  List<Object?> get props => [showSpinner];
}

/// Expands (or collapses) one title's panel. One at a time, like Failed Updates: two open
/// instructions panels would be two AI refreshes polling with nothing saying which was whose.
final class SoftwareRowToggled extends SoftwareEvent {
  const SoftwareRowToggled(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class SoftwareTitleAdded extends SoftwareEvent {
  const SoftwareTitleAdded(this.draft);

  final SoftwareTitleDraft draft;

  @override
  List<Object?> get props => [draft];
}

final class SoftwareTitleSaved extends SoftwareEvent {
  const SoftwareTitleSaved(this.id, {required this.mode, this.detectAs, required this.secretNames});

  final String id;
  final SoftwareMode mode;
  final String? detectAs;
  final List<String> secretNames;

  @override
  List<Object?> get props => [id, mode, detectAs, secretNames];
}

final class SoftwareAssignmentSaved extends SoftwareEvent {
  const SoftwareAssignmentSaved(this.id, {required this.allHosts, required this.hostNames});

  final String id;
  final bool allHosts;
  final List<String> hostNames;

  @override
  List<Object?> get props => [id, allHosts, hostNames];
}

final class SoftwareRunNowRequested extends SoftwareEvent {
  const SoftwareRunNowRequested(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

final class SoftwareTitleDeleted extends SoftwareEvent {
  const SoftwareTitleDeleted(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

/// Sets or replaces a secret's value.
///
/// The only object in this screen that ever holds a secret value, and it holds it for exactly as
/// long as the event is being handled: the value is handed to the use case and never copied into
/// [SoftwareState]. It is left out of [props] and [toString] so nothing that prints, compares or
/// records events — a `BlocObserver`, a test failure message — can carry it anywhere.
final class SoftwareSecretSet extends SoftwareEvent {
  const SoftwareSecretSet(this.name, this.value);

  final String name;
  final String value;

  @override
  List<Object?> get props => [name];

  @override
  String toString() => 'SoftwareSecretSet($name, [redacted])';
}

final class SoftwareSecretDeleted extends SoftwareEvent {
  const SoftwareSecretDeleted(this.name);

  final String name;

  @override
  List<Object?> get props => [name];
}

class SoftwareState extends Equatable {
  const SoftwareState({
    this.loading = true,
    this.overview,
    this.expandedId,
    this.busy = false,
    this.error,
    this.notice,
    this.savedSecretName,
  });

  final bool loading;

  /// Null until the first read answers.
  final SoftwareOverview? overview;
  final String? expandedId;

  /// A write is in flight. One flag for the whole screen rather than one per row: every write
  /// answers with the whole screen, so two in flight would race to overwrite each other's answer.
  final bool busy;
  final String? error;
  final String? notice;

  /// The secret whose value was just accepted, so the row's field knows to clear itself — set on
  /// success only, so a refused value stays typed for the administrator to fix. A name, never a
  /// value.
  final String? savedSecretName;

  List<SoftwareTitle> get titles => overview?.titles ?? const [];
  List<ScriptSecretSummary> get secrets => overview?.secrets ?? const [];

  SoftwareState copyWith({
    bool? loading,
    SoftwareOverview? overview,
    String? expandedId,
    bool? busy,
    String? error,
    String? notice,
    String? savedSecretName,
    bool clearExpanded = false,
    bool clearMessages = false,
  }) =>
      SoftwareState(
        loading: loading ?? this.loading,
        overview: overview ?? this.overview,
        expandedId: clearExpanded ? null : (expandedId ?? this.expandedId),
        busy: busy ?? this.busy,
        error: clearMessages ? null : (error ?? this.error),
        notice: clearMessages ? null : (notice ?? this.notice),
        savedSecretName: clearMessages ? null : (savedSecretName ?? this.savedSecretName),
      );

  @override
  List<Object?> get props => [loading, overview, expandedId, busy, error, notice, savedSecretName];
}

/// The Software screen.
///
/// No polling: nothing about a title changes on the server while the screen is open except as the
/// fleet checks in, which is hourly, so a Reload button says what it does instead. The one thing
/// that does run in the background — an AI research run in a custom title's instructions panel —
/// polls itself and asks for a reload, the way Failed Updates does.
class SoftwareBloc extends Bloc<SoftwareEvent, SoftwareState> {
  SoftwareBloc({
    required GetSoftware getSoftware,
    required AddSoftwareTitle addTitle,
    required UpdateSoftwareTitle updateTitle,
    required AssignSoftwareTitle assignTitle,
    required RunSoftwareTitleNow runNow,
    required DeleteSoftwareTitle deleteTitle,
    required SetScriptSecret setSecret,
    required DeleteScriptSecret deleteSecret,
  })  : _getSoftware = getSoftware,
        _addTitle = addTitle,
        _updateTitle = updateTitle,
        _assignTitle = assignTitle,
        _runNow = runNow,
        _deleteTitle = deleteTitle,
        _setSecret = setSecret,
        _deleteSecret = deleteSecret,
        super(const SoftwareState()) {
    on<SoftwareRequested>(_onRequested);
    on<SoftwareRowToggled>(
      (event, emit) => emit(
        state.expandedId == event.id ? state.copyWith(clearExpanded: true) : state.copyWith(expandedId: event.id),
      ),
    );
    on<SoftwareTitleAdded>(
      (event, emit) => _write(emit, () => _addTitle(event.draft), 'Added ${event.draft.name}.'),
    );
    on<SoftwareTitleSaved>(
      (event, emit) => _write(
        emit,
        () => _updateTitle(event.id, mode: event.mode, detectAs: event.detectAs, secretNames: event.secretNames),
        'Saved ${_nameOf(event.id)}.',
      ),
    );
    on<SoftwareAssignmentSaved>(
      (event, emit) => _write(
        emit,
        () => _assignTitle(event.id, allHosts: event.allHosts, hostNames: event.hostNames),
        'Saved who ${_nameOf(event.id)} applies to. Hosts act at their next hourly check-in, or press Run now.',
      ),
    );
    on<SoftwareRunNowRequested>(_onRunNow);
    on<SoftwareTitleDeleted>((event, emit) async {
      final name = _nameOf(event.id);
      await _write(
        emit,
        () => _deleteTitle(event.id),
        'Deleted $name. Nothing already installed or removed was undone.',
        closePanel: true,
      );
    });
    on<SoftwareSecretSet>(
      (event, emit) => _write(
        emit,
        () => _setSecret(event.name, event.value),
        'Saved the value of ${event.name}. It cannot be shown again; replace it to change it.',
        savedSecretName: event.name,
      ),
    );
    on<SoftwareSecretDeleted>(
      (event, emit) => _write(emit, () => _deleteSecret(event.name), 'Deleted ${event.name}.'),
    );
  }

  final GetSoftware _getSoftware;
  final AddSoftwareTitle _addTitle;
  final UpdateSoftwareTitle _updateTitle;
  final AssignSoftwareTitle _assignTitle;
  final RunSoftwareTitleNow _runNow;
  final DeleteSoftwareTitle _deleteTitle;
  final SetScriptSecret _setSecret;
  final DeleteScriptSecret _deleteSecret;

  String _nameOf(String id) {
    final matching = state.titles.where((t) => t.id == id);
    return matching.isEmpty ? 'the title' : matching.first.name;
  }

  Future<void> _onRequested(SoftwareRequested event, Emitter<SoftwareState> emit) async {
    emit(state.copyWith(loading: event.showSpinner ? true : null, clearMessages: true));
    try {
      emit(state.copyWith(loading: false, overview: await _getSoftware()));
    } on ApiException catch (error) {
      emit(state.copyWith(loading: false, error: describeSoftwareError(error)));
    } catch (_) {
      // An answer the mapper cannot read must still clear `loading`, or the spinner never stops.
      emit(state.copyWith(loading: false, error: unreadableSoftwareAnswer));
    }
  }

  /// Runs one write and adopts the screen state it answers with.
  Future<void> _write(
    Emitter<SoftwareState> emit,
    Future<SoftwareOverview> Function() action,
    String notice, {
    bool closePanel = false,
    String? savedSecretName,
  }) async {
    emit(state.copyWith(busy: true, clearMessages: true));
    try {
      final overview = await action();
      emit(state.copyWith(
        busy: false,
        loading: false,
        overview: overview,
        notice: notice,
        savedSecretName: savedSecretName,
        clearExpanded: closePanel,
      ));
    } on ApiException catch (error) {
      emit(state.copyWith(busy: false, error: describeSoftwareError(error)));
    } catch (_) {
      // An answer the mapper cannot read must still clear `busy`, which locks every button.
      emit(state.copyWith(busy: false, error: unreadableSoftwareAnswer));
    }
  }

  Future<void> _onRunNow(SoftwareRunNowRequested event, Emitter<SoftwareState> emit) async {
    final name = _nameOf(event.id);
    emit(state.copyWith(busy: true, clearMessages: true));
    final SoftwareRunNowResult outcome;
    try {
      outcome = await _runNow(event.id);
    } on ApiException catch (error) {
      // The server refuses a Self-service title and one whose script is not Ready (IP-880). The
      // screen disables the button for both, but its state can be stale, so the refusal is named
      // as one rather than shown as a bare message.
      emit(state.copyWith(busy: false, error: 'Run now was refused for $name: ${describeSoftwareError(error)}'));
      return;
    } catch (_) {
      emit(state.copyWith(busy: false, error: unreadableSoftwareAnswer));
      return;
    }
    final notice = describeRunNow(name, outcome.result);
    try {
      emit(state.copyWith(
        busy: false,
        loading: false,
        overview: outcome.overview ?? await _getSoftware(),
        notice: notice,
      ));
    } on ApiException catch (error) {
      // The hosts were already asked to act, so this must not read as a refusal: an admin told
      // the run failed would press it again.
      emit(state.copyWith(
        busy: false,
        notice: notice,
        error: 'Hosts were asked to act, but the screen could not reload: '
            '${describeSoftwareError(error)} Press Reload.',
      ));
    } catch (_) {
      emit(state.copyWith(busy: false, notice: notice, error: unreadableSoftwareAnswer));
    }
  }
}

/// Shown when the server answered but not in a shape the mapper accepts.
const unreadableSoftwareAnswer = "The server's answer could not be read. Press Reload.";

/// The server's refusal in words: its field-level messages where it sent some, which is where a
/// duplicate name or a secret still in use is explained.
String describeSoftwareError(ApiException error) => error.validationErrors.isEmpty
    ? error.message
    : error.validationErrors.values.expand((messages) => messages).join(' ');

/// What Run now says. Like "Patch now", it promises an instruction rather than an outcome — the
/// counts do not move until the hosts act and check in — and it names every host it could not
/// instruct rather than reporting a bare number.
String describeRunNow(String name, ForcedPatchRunResult result) {
  final asked = switch (result.requested) {
    0 => 'No host was pending or failed on $name, so nothing was asked to act.',
    1 => 'Asked 1 host to act on $name now.',
    final count => 'Asked $count hosts to act on $name now.',
  };
  final when = result.requested == 0
      ? ''
      : ' A host with someone logged in acts within about a minute; one with nobody logged in acts at '
          'its next hourly check-in. The counts change once they report back.';
  final missed = result.notRequested.isEmpty ? '' : ' Not asked: ${result.notRequested.join(', ')}.';
  return '$asked$when$missed';
}
