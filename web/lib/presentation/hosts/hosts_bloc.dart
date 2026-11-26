import 'package:bloc/bloc.dart';
import 'package:equatable/equatable.dart';

import '../../core/bloc/polling.dart';
import '../../core/network/api_exception.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';
import 'hosts_filters.dart';

sealed class HostsEvent extends Equatable {
  const HostsEvent();

  @override
  List<Object?> get props => const [];
}

final class HostsRequested extends HostsEvent {
  const HostsRequested({this.showSpinner = true});

  /// False for a background poll, so a refresh does not blank the table that is already on screen.
  final bool showSpinner;

  @override
  List<Object?> get props => [showSpinner];
}

final class HostRemovalRequested extends HostsEvent {
  const HostRemovalRequested(this.host);

  final HostSummary host;

  @override
  List<Object?> get props => [host];
}

final class HostsMessageDismissed extends HostsEvent {
  const HostsMessageDismissed();
}

final class HostsSearchChanged extends HostsEvent {
  const HostsSearchChanged(this.search);

  final String search;

  @override
  List<Object?> get props => [search];
}

final class HostsAgentFilterChanged extends HostsEvent {
  const HostsAgentFilterChanged(this.filter);

  /// One of [hostsAgentFilterOptions]'s keys.
  final String filter;

  @override
  List<Object?> get props => [filter];
}

/// Sets one field's filter to exactly [values]; none removes it. Used by the "+ Add filter" menu.
final class HostsFilterValuesSet extends HostsEvent {
  const HostsFilterValuesSet(this.field, this.values);

  final HostFilterField field;
  final Set<String> values;

  @override
  List<Object?> get props => [field, values];
}

/// Adds or removes one value of a field. A click on a group's name in "Where it fails".
final class HostsFilterToggled extends HostsEvent {
  const HostsFilterToggled(this.field, this.value);

  final HostFilterField field;
  final String value;

  @override
  List<Object?> get props => [field, value];
}

final class HostsFilterRemoved extends HostsEvent {
  const HostsFilterRemoved(this.field);

  final HostFilterField field;

  @override
  List<Object?> get props => [field];
}

final class HostsFiltersCleared extends HostsEvent {
  const HostsFiltersCleared();
}

/// A click on a coloured part of a bar: the group and the health state at once, or both removed when
/// they are already applied.
final class HostsSegmentToggled extends HostsEvent {
  const HostsSegmentToggled(this.field, this.group, this.health);

  final HostFilterField field;
  final String group;
  final HostHealthState health;

  @override
  List<Object?> get props => [field, group, health];
}

final class HostsGroupByChanged extends HostsEvent {
  const HostsGroupByChanged(this.groupBy);

  final HostGroupBy groupBy;

  @override
  List<Object?> get props => [groupBy];
}

/// A header was pressed. The same column again reverses the order; another column starts in its
/// natural order.
final class HostsSortChanged extends HostsEvent {
  const HostsSortChanged(this.key);

  final String key;

  @override
  List<Object?> get props => [key];
}

final class HostsSelectionToggled extends HostsEvent {
  const HostsSelectionToggled(this.id);

  final String id;

  @override
  List<Object?> get props => [id];
}

/// The header checkbox: every host the filters leave on screen, or none.
final class HostsSelectAllChanged extends HostsEvent {
  const HostsSelectAllChanged(this.selected);

  final bool selected;

  @override
  List<Object?> get props => [selected];
}

final class HostsSelectionCleared extends HostsEvent {
  const HostsSelectionCleared();
}

/// Sets department, location or organization on [hostIds]. A null field is kept; an empty string
/// clears it.
final class HostsProfileUpdateRequested extends HostsEvent {
  const HostsProfileUpdateRequested(this.hostIds, {this.department, this.location, this.organization});

  final List<String> hostIds;
  final String? department;
  final String? location;
  final String? organization;

  @override
  List<Object?> get props => [hostIds, department, location, organization];
}

/// An import finished on the server: reload the list so the new departments and locations show.
final class HostsImportApplied extends HostsEvent {
  const HostsImportApplied(this.message);

  final String message;

  @override
  List<Object?> get props => [message];
}

/// The Agent filter's choices: every host, those behind the newest agent for their platform, and
/// those the server thinks are stuck there.
const hostsAgentFilterOptions = <String, String>{
  'all': 'All hosts',
  'behind': 'Behind the latest',
  'stuck': 'Probably stuck',
};

final class HostsState extends Equatable {
  const HostsState({
    this.hosts = const [],
    this.search = '',
    this.agentFilter = 'all',
    this.filters = const HostFilters(),
    this.groupBy = HostGroupBy.department,
    this.sort = hostDefaultSort,
    this.selectedIds = const {},
    this.loading = true,
    this.error,
    this.notice,
    this.removingId,
  });

  /// Every host the server returned. The table shows [visibleHosts]; the subtitle counts these.
  final List<HostSummary> hosts;

  /// The text in the Hostname column's search box. Applied client-side — everything it needs is
  /// already in the response, so there is no round trip, and a poll that arrives mid-typing
  /// replaces [hosts] without touching this.
  final String search;

  /// One of [hostsAgentFilterOptions]'s keys.
  final String agentFilter;

  /// The filters chosen from "+ Add filter", from a bar in "Where it fails" or from a group's name.
  final HostFilters filters;

  /// What "Where it fails" groups its bars by.
  final HostGroupBy groupBy;

  /// How the table is ordered. Starts as [hostDefaultSort]: connected hosts first, then the rest by
  /// most recent check-in.
  final TableSort sort;

  /// The hosts ticked for a bulk edit. Kept across filter changes so the choice survives narrowing
  /// the table, and trimmed to hosts that still exist whenever the list reloads.
  final Set<String> selectedIds;

  final bool loading;
  final String? error;
  final String? notice;

  /// The host whose removal is in flight, so its button can be disabled without disabling every
  /// other row's.
  final String? removingId;

  /// The hosts the search leaves on screen. Matches everything an administrator identifies a host by
  /// — its name, serial number, last logged-in user, address and operating system —
  /// case-insensitively, as a substring, so a fragment of a hostname, the tail of a serial or a
  /// person's name is enough to find one machine in a fleet.
  List<HostSummary> get visibleHosts => sortHosts(_matching(const {}), sort);

  /// The hosts the search, the Agent filter and the chosen filters leave, except that the fields in
  /// [except] are not applied. "Where it fails" uses this with the field it groups by and the health
  /// field excluded, so applying a bar's segment (which sets both) does not shrink the bars to the
  /// part just chosen.
  List<HostSummary> _matching(Set<HostFilterField> except) {
    final needle = search.toLowerCase();
    return [
      for (final host in hosts)
        if (_matchesAgent(host) &&
            filters.matches(host, except: except) &&
            (search.isEmpty ||
                [host.hostname, host.serialNumber, host.lastLoggedInUser, host.ipAddress, host.operatingSystem]
                    .any((field) => field?.toLowerCase().contains(needle) ?? false)))
          host,
    ];
  }

  /// The bars of "Where it fails": the hosts that pass everything except the grouping field and
  /// health, grouped by [groupBy] and counted by state.
  List<HostGroup> get groups =>
      groupHostsByHealth(_matching({groupBy.field, HostFilterField.health}), groupBy.field);

  /// How many hosts the search, the Agent filter and the chosen filters leave in each state: the
  /// tiles across the top.
  int countIn(HostHealthState state) => _matching(const {}).where((h) => h.health == state).length;

  int get withoutPlace => _matching(const {}).where((h) => h.department == null || h.location == null).length;

  /// Whether anything narrows the table: the search, the Agent filter or a chosen filter.
  bool get isNarrowed => search.isNotEmpty || agentFilter != 'all' || !filters.isEmpty;

  /// The hosts ticked **and still on screen**.
  bool get allVisibleSelected {
    final visible = visibleHosts;
    return visible.isNotEmpty && visible.every((h) => selectedIds.contains(h.id));
  }

  bool _matchesAgent(HostSummary host) => switch (agentFilter) {
        'behind' => host.agentUpdateState.isBehind,
        'stuck' => host.agentUpdateState == AgentUpdateState.notUpdating,
        _ => true,
      };

  /// Hosts behind the newest agent for their platform, and of those the ones that look stuck — the
  /// banner above the table. Counted over every host, not the filtered ones, so choosing a filter
  /// never changes the number it was chosen from.
  int get behindCount => hosts.where((h) => h.agentUpdateState.isBehind).length;
  int get stuckCount => hosts.where((h) => h.agentUpdateState == AgentUpdateState.notUpdating).length;

  HostsState copyWith({
    List<HostSummary>? hosts,
    String? search,
    String? agentFilter,
    HostFilters? filters,
    HostGroupBy? groupBy,
    TableSort? sort,
    Set<String>? selectedIds,
    bool? loading,
    String? error,
    String? notice,
    String? removingId,
    bool clearError = false,
    bool clearNotice = false,
    bool clearRemoving = false,
  }) =>
      HostsState(
        hosts: hosts ?? this.hosts,
        search: search ?? this.search,
        agentFilter: agentFilter ?? this.agentFilter,
        filters: filters ?? this.filters,
        groupBy: groupBy ?? this.groupBy,
        sort: sort ?? this.sort,
        selectedIds: selectedIds ?? this.selectedIds,
        loading: loading ?? this.loading,
        error: clearError ? null : (error ?? this.error),
        notice: clearNotice ? null : (notice ?? this.notice),
        removingId: clearRemoving ? null : (removingId ?? this.removingId),
      );

  @override
  List<Object?> get props => [
        hosts,
        search,
        agentFilter,
        filters,
        groupBy,
        sort.key,
        sort.ascending,
        selectedIds,
        loading,
        error,
        notice,
        removingId,
      ];
}

/// The Hosts screen.
///
/// Polls, which the page it replaces did not: a host's status and last-seen move on their own as
/// agents check in, and watching them arrive is the point of the screen. Thirty seconds rather
/// than the three the background-run screens use — nothing here changes faster than a check-in
/// interval, and the states are value-compared, so a poll that finds nothing new emits an
/// identical state and rebuilds nothing.
class HostsBloc extends Bloc<HostsEvent, HostsState> with Polling<HostsEvent, HostsState> {
  HostsBloc({
    required GetHosts getHosts,
    required RequestHostRemoval requestHostRemoval,
    required UpdateHostProfiles updateHostProfiles,
    HostFilters initialFilters = const HostFilters(),
  })  : _getHosts = getHosts,
        _requestHostRemoval = requestHostRemoval,
        _updateHostProfiles = updateHostProfiles,
        super(HostsState(filters: initialFilters)) {
    on<HostsRequested>(_onRequested);
    on<HostRemovalRequested>(_onRemovalRequested);
    on<HostsMessageDismissed>(
      (_, emit) => emit(state.copyWith(clearError: true, clearNotice: true)),
    );
    on<HostsSearchChanged>((event, emit) => emit(state.copyWith(search: event.search)));
    on<HostsAgentFilterChanged>((event, emit) => emit(state.copyWith(agentFilter: event.filter)));
    on<HostsFilterValuesSet>((event, emit) =>
        emit(state.copyWith(filters: state.filters.withValues(event.field, event.values))));
    on<HostsFilterToggled>((event, emit) =>
        emit(state.copyWith(filters: state.filters.toggled(event.field, event.value))));
    on<HostsFilterRemoved>((event, emit) => emit(state.copyWith(filters: state.filters.without(event.field))));
    on<HostsFiltersCleared>((_, emit) => emit(state.copyWith(filters: const HostFilters())));
    on<HostsSegmentToggled>((event, emit) => emit(state.copyWith(
          filters: state.filters.segmentToggled(event.field, event.group, event.health),
        )));
    on<HostsGroupByChanged>((event, emit) => emit(state.copyWith(groupBy: event.groupBy)));
    on<HostsSortChanged>((event, emit) => emit(state.copyWith(
          sort: state.sort.key == event.key ? state.sort.toggled() : TableSort(event.key),
        )));
    on<HostsSelectionToggled>((event, emit) {
      final next = {...state.selectedIds};
      if (!next.remove(event.id)) next.add(event.id);
      emit(state.copyWith(selectedIds: next));
    });
    on<HostsSelectAllChanged>((event, emit) {
      final ids = {for (final host in state.visibleHosts) host.id};
      emit(state.copyWith(
        selectedIds: event.selected ? {...state.selectedIds, ...ids} : state.selectedIds.difference(ids),
      ));
    });
    on<HostsSelectionCleared>((_, emit) => emit(state.copyWith(selectedIds: const {})));
    on<HostsProfileUpdateRequested>(_onProfileUpdateRequested);
    on<HostsImportApplied>((event, emit) {
      emit(state.copyWith(notice: event.message, clearError: true));
      add(const HostsRequested(showSpinner: false));
    });

    startPolling(const Duration(seconds: 30), const HostsRequested(showSpinner: false));
  }

  final GetHosts _getHosts;
  final RequestHostRemoval _requestHostRemoval;
  final UpdateHostProfiles _updateHostProfiles;

  Future<void> _onRequested(HostsRequested event, Emitter<HostsState> emit) async {
    if (event.showSpinner) emit(state.copyWith(loading: true, clearError: true));

    try {
      final hosts = await _getHosts();
      final ids = {for (final host in hosts) host.id};
      emit(state.copyWith(
        hosts: hosts,
        selectedIds: state.selectedIds.intersection(ids),
        loading: false,
        clearError: true,
      ));
    } on ApiException catch (error) {
      // A failed poll leaves the table alone and says so. Replacing a working list with an error
      // because one background refresh missed would be worse than showing slightly stale data.
      emit(state.copyWith(loading: false, error: error.message));
    }
  }

  Future<void> _onRemovalRequested(HostRemovalRequested event, Emitter<HostsState> emit) async {
    emit(state.copyWith(removingId: event.host.id, clearError: true, clearNotice: true));

    try {
      await _requestHostRemoval(event.host.id);
      emit(state.copyWith(
        clearRemoving: true,
        notice: '${event.host.hostname} will uninstall its agent on its next check-in.',
      ));
      add(const HostsRequested(showSpinner: false));
    } on ApiException catch (error) {
      emit(state.copyWith(
        clearRemoving: true,
        error: 'Could not remove ${event.host.hostname}: ${error.message}',
      ));
    }
  }

  Future<void> _onProfileUpdateRequested(HostsProfileUpdateRequested event, Emitter<HostsState> emit) async {
    emit(state.copyWith(clearError: true, clearNotice: true));
    try {
      final updated = await _updateHostProfiles(
        hostIds: event.hostIds,
        department: event.department,
        location: event.location,
        organization: event.organization,
      );
      emit(state.copyWith(selectedIds: const {}, notice: 'Updated $updated host${updated == 1 ? '' : 's'}.'));
      add(const HostsRequested(showSpinner: false));
    } on ApiException catch (error) {
      emit(state.copyWith(error: 'Could not update the hosts: ${error.message}'));
    }
  }
}
