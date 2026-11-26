import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/platform/page_navigator.dart';
import '../../core/router/app_router.dart';
import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/form_bits.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/enums.dart';
import '../../domain/entities/host.dart';
import '../../domain/usecases/host_usecases.dart';
import 'host_details_panel.dart';
import 'host_hostname_cell.dart';
import 'host_profile_dialogs.dart';
import 'hosts_bloc.dart';
import 'hosts_filter_bar.dart';
import 'hosts_filters.dart';
import 'hosts_format.dart';
import 'hosts_overview.dart';

/// Every host in the fleet. What `Pages/Hosts.cshtml` was.
///
/// From top to bottom: the five tiles, "Where it fails" (one thin bar per department, location, system
/// or model), the filters the administrator chose, and the table. Every column header sorts the table
/// (press it again to reverse), and it opens with connected hosts first, then the rest by how recently
/// they checked in.
class HostsScreen extends StatelessWidget {
  const HostsScreen({super.key, this.initialFilters = const HostFilters()});

  /// Filters already applied when the page opens, from a link such as the Autonomy page's rings.
  final HostFilters initialFilters;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => HostsBloc(
          getHosts: locator<GetHosts>(),
          requestHostRemoval: locator<RequestHostRemoval>(),
          updateHostProfiles: locator<UpdateHostProfiles>(),
          initialFilters: initialFilters,
        )..add(const HostsRequested()),
        child: const _HostsView(),
      );
}

/// The inset of every cell, each side. Half the default: twelve columns is more than the page has room for
/// at the default, and a column pays it twice.
const _tableGutter = 6.0;

/// A column that never goes below [min] (the width its cell needs on one line) and shares what is left
/// over with the others, in proportion to [flex]. At the page's width that is every column visible
/// without a sideways scroll; below the sum of the minimums the table scrolls instead of squeezing.
class _ColumnWidth extends MaxColumnWidth {
  _ColumnWidth(double min, double flex) : super(FixedColumnWidth(min), FlexColumnWidth(flex));
}

/// How many hosts are behind the newest agent for their platform, and a way to see them.
///
/// Silent when none is, rather than a green "all current" bar on every visit: the Hosts screen is
/// opened for everything else, and a banner that is there when nothing is wrong teaches people to
/// stop reading it. Red when some are probably stuck, because that is the one that needs somebody.
class _AgentDriftBanner extends StatelessWidget {
  const _AgentDriftBanner({required this.state});

  final HostsState state;

  @override
  Widget build(BuildContext context) {
    final bloc = context.read<HostsBloc>();
    final stuck = state.stuckCount;
    final behind = state.behindCount;
    if (behind == 0) return const SizedBox.shrink();

    final text = stuck > 0
        ? '$stuck host${stuck == 1 ? ' is' : 's are'} online but still on an old agent, a day or more '
            'after the new one was published. The automatic update is probably failing there.'
        : '$behind host${behind == 1 ? ' is' : 's are'} behind the latest agent. Online ones update '
            'themselves at their next hourly check-in, so this usually clears on its own.';

    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Expanded(child: stuck > 0 ? AlertBox.error(text) : AlertBox.info(text)),
          const SizedBox(width: 12),
          SecondaryButton(
            label: 'Show them',
            onPressed: () => bloc.add(HostsAgentFilterChanged(stuck > 0 ? 'stuck' : 'behind')),
          ),
        ],
      ),
    );
  }
}

class _HostsView extends StatefulWidget {
  const _HostsView();

  @override
  State<_HostsView> createState() => _HostsViewState();
}

class _HostsViewState extends State<_HostsView> {
  /// The table and the line above it, so a click on a coloured part of a bar can scroll them into view.
  final _tableKey = GlobalKey();
  final _hover = HostRowHover();

  /// Set by a click that *applies* a bar's segment, and spent by the next change of filters: the page
  /// scrolls to the hosts only after the table has been rebuilt for them.
  bool _scrollAfterFilter = false;

  @override
  void dispose() {
    _hover.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => BlocListener<HostsBloc, HostsState>(
        listenWhen: (before, after) => before.filters != after.filters,
        listener: (context, _) {
          if (!_scrollAfterFilter) return;
          _scrollAfterFilter = false;
          WidgetsBinding.instance.addPostFrameCallback((_) {
            final target = _tableKey.currentContext;
            if (target != null && target.mounted) {
              Scrollable.ensureVisible(target, duration: const Duration(milliseconds: 250), curve: Curves.easeOut);
            }
          });
        },
        child: BlocBuilder<HostsBloc, HostsState>(builder: (context, state) => _buildBody(context, state)),
      );

  void _onSegment(HostsState state, String group, HostHealthState health) {
    final field = state.groupBy.field;
    if (!state.filters.segmentActive(field, group, health)) _scrollAfterFilter = true;
    context.read<HostsBloc>().add(HostsSegmentToggled(field, group, health));
  }

  Future<void> _import() async {
    final bloc = context.read<HostsBloc>();
    final message = await showHostImportDialog(context);
    if (message != null) bloc.add(HostsImportApplied(message));
  }

  Future<void> _bulkEdit(HostsState state) async {
    final bloc = context.read<HostsBloc>();
    final choice = await showHostProfileDialog(
      context,
      count: state.selectedIds.length,
      departments: [for (final e in countHostValues(HostFilterField.department, state.hosts)) if (e.key != hostNotSet) e.key],
      locations: [for (final e in countHostValues(HostFilterField.location, state.hosts)) if (e.key != hostNotSet) e.key],
    );
    if (choice == null || choice.isEmpty) return;
    bloc.add(HostsProfileUpdateRequested(
      state.selectedIds.toList(),
      department: choice.department,
      location: choice.location,
    ));
  }

  void _openDetails(HostSummary host) => showHostDetails(
        context,
        host: host,
        onAction: (action) {
          if (!mounted) return;
          switch (action) {
            case HostPanelAction.terminal:
              context.go(_withHostname(Routes.remoteShell(host.id), host));
            case HostPanelAction.connect:
              context.go(_withHostname(Routes.remoteControl(host.id), host));
            case HostPanelAction.remove:
              _confirmRemoval(context, host);
            case HostPanelAction.hostPage:
              context.go(Routes.hostActivity(host.id));
          }
        },
      );

  static String _withHostname(String path, HostSummary host) =>
      Uri(path: path, queryParameters: {'hostname': host.hostname}).toString();

  Widget _buildBody(BuildContext context, HostsState state) {
    final bloc = context.read<HostsBloc>();
    final columns = <TableColumnSpec>[
      TableColumnSpec(
        label: 'Select',
        width: const FixedColumnWidth(44),
        headerContent: Checkbox(
          key: const Key('select-all'),
          value: state.allVisibleSelected,
          onChanged: (value) => bloc.add(HostsSelectAllChanged(value ?? false)),
        ),
      ),
      TableColumnSpec(
        label: 'Hostname',
        // Never below the width of a long hostname with the copy icon beside it; one line.
        width: _ColumnWidth(156, 1.5),
        sortKey: HostSortKey.hostname,
        // Under the Hostname header, where the Applications table keeps its own search, though it
        // matches serials, users, addresses and operating systems too — see
        // `HostsState.visibleHosts`. A fleet of a few hundred hosts is past scrolling for one, and
        // the hostname is what somebody arrives knowing.
        filter: SearchField(
          value: state.search,
          hintText: 'Search hosts...',
          onChanged: (value) => bloc.add(HostsSearchChanged(value)),
        ),
      ),
      // Monospace, one line, an ellipsis rather than a wrap: a serial that breaks onto a second line
      // makes every row taller and cannot be read as a unit.
      TableColumnSpec(label: 'Serial', width: _ColumnWidth(100, 1.0), sortKey: HostSortKey.serial),
      // The model, with the operating system on the line beneath it. **The model is blank until an import
      // or a future agent report supplies it**: no agent reports one yet, so for now it comes only from the
      // Snipe-IT import, and a host without one shows no dash and no "not set" here, just its system.
      TableColumnSpec(label: 'Model', width: _ColumnWidth(140, 1.5), sortKey: HostSortKey.model),
      TableColumnSpec(label: 'Department', width: _ColumnWidth(150, 1.0), sortKey: HostSortKey.department),
      TableColumnSpec(label: 'Location', width: _ColumnWidth(126, 1.0), sortKey: HostSortKey.location),
      TableColumnSpec(label: 'Health', width: _ColumnWidth(120, 1.4), sortKey: HostSortKey.health),
      const TableColumnSpec(label: 'Updates', width: FixedColumnWidth(114), alignRight: true, sortKey: HostSortKey.updates),
      // ONE column for CVEs, where there were two. The badge is the count *not confirmed fixed* (see
      // `HostSummary.unpatchedCveCount`): the one that is an alert, the same number and tooltip the old
      // alert column had. The count confirmed already up to date is in its tooltip and, with the rest of
      // what the table has no room for, in the details panel. "Unpatched" is the wider bucket: an
      // application with no researched/confirmed-current verdict lands here rather than being dropped as
      // a coverage gap, and a distribution package follows the host's own OS update state.
      const TableColumnSpec(label: 'CVEs', width: FixedColumnWidth(84), alignRight: true, sortKey: HostSortKey.cves),
      // Measured rather than guessed, because "Decommissioned" is one fourteen-character word and
      // a chip cannot be made narrower than that: at a hand-set 140 it broke mid-word, the way
      // the CVE Reporting table's Exploited column did. `KintsugiTable` floors a column at its
      // *header* word only, so nothing else would have caught it.
      TableColumnSpec(
        label: 'Status',
        sortKey: HostSortKey.status,
        width: TableColumnSpec.forContent(
          HostStatus.values
              .map((status) => StatusChip.widthFor(context, status.label))
              .reduce(math.max),
          gutter: _tableGutter,
        ),
      ),
      // "Connected" for a host that has checked in within the last interval, else how long ago it did.
      // Sorting it is the page's default order.
      TableColumnSpec(label: 'Last uptime', width: _ColumnWidth(102, 1.0), sortKey: HostSortKey.lastUptime),
      // Three icons (Connect, Terminal and Remove): three 40px buttons and two 8px gaps is 136, and the
      // gutters take 12 more. A `Wrap` that cannot fit its children does not shrink them, it breaks to a
      // second line and makes every row taller, so this is exactly the room they need and no less.
      const TableColumnSpec(label: 'Actions', width: FixedColumnWidth(152)),
    ];

    final visible = state.visibleHosts;
    final narrowedByFilters = !state.filters.isEmpty || state.agentFilter != 'all';

    return PageScaffold(
      title: 'Registered Hosts',
      subtitle: !state.isNarrowed
          ? '${state.hosts.length} host(s) registered'
          : '${visible.length} of ${state.hosts.length} host(s) match the ${narrowedByFilters ? 'filters' : 'search'}',
      children: [
        if (state.error != null) AlertBox.error(state.error!),
        if (state.notice != null) AlertBox.success(state.notice!),
        _AgentDriftBanner(state: state),
        if (state.loading && state.hosts.isEmpty)
          const _Loading()
        else if (state.hosts.isEmpty)
          const EmptyPanel(
            'No hosts have been registered yet. A host appears here once its agent enrolls and '
            'reports in.',
          )
        else ...[
          HostsTiles(state: state),
          const SizedBox(height: 14),
          WhereItFails(
            state: state,
            onGroupBy: (option) => bloc.add(HostsGroupByChanged(option)),
            onGroup: (group) => bloc.add(HostsFilterToggled(state.groupBy.field, group)),
            onSegment: (group, health) => _onSegment(state, group, health),
          ),
          const SizedBox(height: 14),
          HostsFilterBar(
            filters: state.filters,
            hosts: state.hosts,
            onSet: (field, values) => bloc.add(HostsFilterValuesSet(field, values)),
            onClear: () => bloc.add(const HostsFiltersCleared()),
            trailing: SecondaryButton(
              key: const Key('import-open'),
              label: 'Import department and location',
              onPressed: _import,
            ),
          ),
          const SizedBox(height: 12),
          if (state.selectedIds.isNotEmpty) ...[
            _BulkBar(
              count: state.selectedIds.length,
              onEdit: () => _bulkEdit(state),
              onClear: () => bloc.add(const HostsSelectionCleared()),
            ),
            const SizedBox(height: 12),
          ],
          // The table and the line above it. A click on a coloured part of a bar scrolls here, so the
          // line says which hosts these are ("HR · Pending").
          Column(
            key: _tableKey,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              _TableTitle(label: state.filters.summaryLabel, shown: visible.length),
              const SizedBox(height: 8),
              // The table's text a size down, which is what the screen's many columns need.
              DefaultTextStyle.merge(
                style: const TextStyle(fontSize: 12.8),
                // The table stays on screen when the search matches nothing, because the search box
                // is in its header — swapping the whole table for an empty panel would take the box
                // away with the text still in it.
                child: KintsugiTable(
                  columns: columns,
                  minWidth: 1480,
                  gutter: _tableGutter,
                  sort: state.sort,
                  onSort: (key) => bloc.add(HostsSortChanged(key)),
                  // Not a column filter: "is this host's agent behind" is a different question from
                  // any one column's, the same reason Failed Updates has its Type filter up here.
                  toolbar: Row(
                    children: [
                      const Expanded(
                        child: HintText(
                          'Search by hostname, serial number, user, address or operating system from the '
                          'Hostname header below. Press a header to sort.',
                        ),
                      ),
                      SizedBox(
                        width: 230,
                        child: KintsugiDropdown<String>(
                          value: state.agentFilter,
                          items: hostsAgentFilterOptions.keys.toList(),
                          labelOf: (value) => 'Agent: ${hostsAgentFilterOptions[value]!}',
                          onChanged: (value) => bloc.add(HostsAgentFilterChanged(value)),
                        ),
                      ),
                    ],
                  ),
                  rows: [
                    for (final host in visible)
                      KintsugiTableRow(
                        key: ValueKey(host.id),
                        cells: [
                          for (final cell in _cells(context, host, state)) HostRowHoverRegion(hostId: host.id, hover: _hover, child: cell),
                        ],
                      ),
                  ],
                ),
              ),
            ],
          ),
          if (visible.isEmpty)
            Padding(
              padding: const EdgeInsets.only(top: 16),
              child: EmptyPanel(narrowedByFilters ? 'No hosts match the filters.' : 'No hosts match the search.'),
            ),
        ],
      ],
    );
  }

  List<Widget> _cells(BuildContext context, HostSummary host, HostsState state) => [
        Checkbox(
          key: Key('select-${host.id}'),
          value: state.selectedIds.contains(host.id),
          onChanged: (_) => context.read<HostsBloc>().add(HostsSelectionToggled(host.id)),
        ),
        // The hostname opens the details panel; the host's activity page is "Open host page" inside it.
        // The last logged-in user sits under the name because the search matches it, and a match on a
        // value nobody can see reads as a broken search.
        Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            HostnameCell(host: host, hover: _hover, onOpen: () => _openDetails(host)),
            if (host.lastLoggedInUser != null)
              Padding(padding: const EdgeInsets.only(top: 4), child: HintText(host.lastLoggedInUser!)),
          ],
        ),
        // One line, ellipsis. The full value is in the hover card and the details panel.
        Text(
          host.serialNumber,
          maxLines: 1,
          overflow: TextOverflow.ellipsis,
          softWrap: false,
          style: AppTheme.mono(color: context.palette.text, size: 12),
        ),
        _ModelCell(key: Key('model-${host.id}'), host: host),
        host.department == null ? const HintText('not set') : Text(host.department!),
        host.location == null ? const HintText('not set') : Text(host.location!),
        _HealthCell(host: host),
        host.appUpdatesAvailableCount > 0
            ? CountBadge(
                host.appUpdatesAvailableCount,
                alert: true,
                tooltip: 'View applications requiring an update on ${host.hostname}',
                // The same deep link the old badge was an <a> to. It carries both filters,
                // because "update available" on its own is fleet-wide and would list every
                // application anyone is behind on rather than this host's.
                onTap: () => context.go(
                  Uri(
                    path: Routes.applications,
                    queryParameters: {'status': 'update-available', 'host': host.hostname},
                  ).toString(),
                ),
              )
            : CountBadge(host.appUpdatesAvailableCount),
        CountBadge(
          host.unpatchedCveCount,
          alert: host.unpatchedCveCount > 0,
          tooltip: 'CVEs on ${host.hostname} not confirmed fixed: applications with an update '
              'available or never researched, and the OS (and every distribution package it '
              'shipped) when an OS update is pending or has never been checked. '
              '${host.patchedCveCount} confirmed already up to date.',
        ),
        // Centred on the chip, not on the column. The cell aligns its child left under loose
        // constraints, so this Column is only as wide as its widest child — the chip — and
        // `center` puts the version under the middle of it. Not `IntrinsicWidth`: on web the
        // chip's intrinsic width comes out narrower than it lays out at and its label breaks
        // mid-word ("ONLIN / E").
        Column(
          crossAxisAlignment: CrossAxisAlignment.center,
          children: [
            Wrap(
              spacing: 6,
              runSpacing: 4,
              children: [
                StatusChip(host.status.label, statusKey: host.status.key),
                if (host.removalRequested)
                  const StatusChip('Removing', statusKey: 'update-available'),
              ],
            ),
            // The agent's own version sits under the status rather than in its own column: it is
            // a property of the same check-in the chip summarises, and a column for a string that
            // is null on every host predating `HostDto.AgentVersion` would mostly show dashes.
            // Same arrangement `_OsUpdateCell` uses for the latest OS version.
            if (host.agentVersion != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: HintText('(${host.agentVersion})'),
              ),
            // Only when it is behind. A host on the latest agent shows nothing extra, so the tag
            // is a thing to notice rather than a column of "OK"s.
            if (host.agentUpdateState.isBehind)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Tooltip(
                  message: host.agentUpdateState == AgentUpdateState.notUpdating
                      ? 'The latest agent for this host is ${host.latestAgentVersion ?? '?'}. It is online, '
                          'and that version has been out for a day or more, so its automatic update is '
                          'probably failing.'
                      : host.status == HostStatus.offline
                          ? 'The latest agent for this host is ${host.latestAgentVersion ?? '?'}. Offline '
                              'hosts update when they next check in.'
                          : 'The latest agent for this host is ${host.latestAgentVersion ?? '?'}. Hosts '
                              'update themselves at their next hourly check-in.',
                  child: StatusChip(
                    host.agentUpdateState == AgentUpdateState.notUpdating
                        ? 'Not updating · ${host.latestAgentVersion ?? '?'}'
                        : 'Behind · ${host.latestAgentVersion ?? '?'}',
                    statusKey: host.agentUpdateState == AgentUpdateState.notUpdating
                        ? 'patch-failed'
                        : 'update-available',
                  ),
                ),
              ),
          ],
        ),
        _LastUptimeCell(host: host),
        Wrap(
          // Three 40px buttons plus two of these gaps is 136, inside the 140 the fixed 152 column
          // leaves after the gutters, so the Wrap never breaks onto a second line. Remove still asks
          // to confirm, which is what stands between a slip and a lost host.
          spacing: 8,
          children: [
            IconActionButton(
              icon: Icons.desktop_windows_outlined,
              tooltip: 'Connect to this host',
              // Offered whatever the host's status says, deliberately. "Online" here means the host
              // checked in within the last interval — up to an hour ago — whereas remote control
              // additionally needs an agent holding a socket right now, which means somebody logged
              // in. Only the server can answer that, and it answers by returning a session already
              // marked unreachable, which the remote-control screen explains. Disabling the button
              // on a stale status would hide a working host instead.
              //
              // Opens in a window of its own, sized by dragging its edges — see
              // `RemoteControlScreen`. Named per host, so a second press reuses that host's window.
              onPressed: host.removalRequested
                  ? null
                  : () => _openSessionWindow(Routes.remoteControl(host.id), 'kintsugi-remote-${host.id}', host),
            ),
            IconActionButton(
              icon: Icons.terminal_outlined,
              tooltip: 'Open a terminal on this host',
              // Offered on the same terms as Connect, and for the same reason: only the server can
              // say whether an agent is holding a socket right now. It is offered on a host with
              // nobody logged in as well, which is not true of a screen session — a terminal needs
              // no desktop, and on Linux and Windows a host with nobody signed in can still provide
              // one. macOS cannot: its agent's per-user process is the half holding the identity,
              // so a Mac with nobody logged in is unreachable for either kind.
              onPressed: host.removalRequested
                  ? null
                  : () => _openSessionWindow(Routes.remoteShell(host.id), 'kintsugi-shell-${host.id}', host),
            ),
            IconActionButton(
              icon: Icons.delete_outline,
              danger: true,
              tooltip: 'Remove host',
              onPressed: state.removingId == host.id ? null : () => _confirmRemoval(context, host),
            ),
          ],
        ),
      ];

  /// `window=1` tells the screen it is in that popup, so it offers "Close Window". `opened` makes
  /// every press a different address: reusing the host's window with the same one would be a hash
  /// change to where it already is, which reloads nothing and leaves an ended session on screen —
  /// the route keys the screen on it, so a new one starts a new session.
  static void _openSessionWindow(String path, String name, HostSummary host) =>
      locator<PageNavigator>().openAppRouteInWindow(
        Uri(path: path, queryParameters: {
          'hostname': host.hostname,
          'window': '1',
          'opened': '${DateTime.now().millisecondsSinceEpoch}',
        }).toString(),
        name: name,
      );

  static Future<void> _confirmRemoval(BuildContext context, HostSummary host) async {
    final bloc = context.read<HostsBloc>();
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        backgroundColor: dialogContext.palette.surface,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(6),
          side: BorderSide(color: dialogContext.palette.border),
        ),
        title: Text('Remove ${host.hostname}?', style: Theme.of(dialogContext).textTheme.titleLarge),
        content: const HintText(
          'Its agent will be instructed to uninstall itself completely on its next check-in. The '
          'host record stays until the agent confirms it has done so.',
        ),
        actions: [
          SecondaryButton(label: 'Cancel', onPressed: () => Navigator.of(dialogContext).pop(false)),
          PrimaryButton(label: 'Remove', onPressed: () => Navigator.of(dialogContext).pop(true)),
        ],
      ),
    );

    if (confirmed == true) bloc.add(HostRemovalRequested(host));
  }
}

/// The tick-box actions bar: how many hosts are ticked and what can be done to all of them.
class _BulkBar extends StatelessWidget {
  const _BulkBar({required this.count, required this.onEdit, required this.onClear});

  final int count;
  final VoidCallback onEdit;
  final VoidCallback onClear;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Container(
      key: const Key('bulk-bar'),
      padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
      decoration: BoxDecoration(
        border: Border.all(color: palette.action),
        borderRadius: BorderRadius.circular(4),
        color: palette.accentWash(0.06),
      ),
      child: Wrap(
        spacing: 12,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          Text('$count selected', style: const TextStyle(fontWeight: FontWeight.w600)),
          PrimaryButton(key: const Key('bulk-edit'), label: 'Set department or location', onPressed: onEdit),
          SecondaryButton(label: 'Clear selection', onPressed: onClear),
        ],
      ),
    );
  }
}

/// "HR · Pending" (or "All hosts") and how many hosts that is: what the table below lists.
class _TableTitle extends StatelessWidget {
  const _TableTitle({required this.label, required this.shown});

  final String label;
  final int shown;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    return Row(
      children: [
        Flexible(
          child: Text(
            label.toUpperCase(),
            key: const Key('table-title'),
            overflow: TextOverflow.ellipsis,
            style: AppTheme.label(color: palette.text, size: 12,),
          ),
        ),
        const SizedBox(width: 12),
        HintText('$shown host${shown == 1 ? '' : 's'}'),
      ],
    );
  }
}

/// The Model cell: the hardware model, and the operating system on the line beneath it.
///
/// A host with no model shows the system alone, in the ordinary text style (as the old Operating System
/// column did) and **nothing in the model's place**: no dash, no "not set". The model is empty until an
/// import or a future agent report supplies it, and a column of placeholders would hide the ones that
/// have been filled in.
class _ModelCell extends StatelessWidget {
  const _ModelCell({super.key, required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) {
    final model = host.model;
    final os = host.operatingSystem;
    if (model == null) return os == null ? const NoValue() : Text(os);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(model),
        if (os != null) Padding(padding: const EdgeInsets.only(top: 2), child: HintText(os)),
      ],
    );
  }
}

/// The Health cell: the state as a green, amber or red chip, and what is behind it in muted text.
class _HealthCell extends StatelessWidget {
  const _HealthCell({required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          StatusChip(host.health.label, statusKey: hostHealthChipKey(host.health)),
          if (host.healthReason != null)
            Padding(
              padding: const EdgeInsets.only(top: 3),
              child: host.failedPatchCount > 0
                  ? FailedPatchesLink(hostname: host.hostname, text: host.healthReason!)
                  : HintText(host.healthReason!),
            ),
          if (host.unstable) const Padding(padding: EdgeInsets.only(top: 4), child: UnstableBadge()),
          if (host.inMaintenance) const Padding(padding: EdgeInsets.only(top: 4), child: MaintenanceBadge()),
        ],
      );
}

/// "Connected" in green for a host that has checked in within the last interval, otherwise how long
/// ago it last did ("28 minutes ago", "3 days ago", "a month ago").
///
/// Agents check in hourly rather than holding a connection open, so "Connected" means "checked in
/// recently", and the tooltip says so.
class _LastUptimeCell extends StatelessWidget {
  const _LastUptimeCell({required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    if (host.isConnected) {
      return Tooltip(
        message: 'Checked in within the last hour',
        child: Text('Connected', style: TextStyle(color: palette.green, fontWeight: FontWeight.w600)),
      );
    }
    final ago = hostRelativeAgo(host.lastSeenUtc);
    return ago == null ? const NoValue() : Tooltip(message: 'Last check-in', child: Text(ago));
  }
}

class _Loading extends StatelessWidget {
  const _Loading();

  @override
  Widget build(BuildContext context) => KintsugiPanel(
        padding: const EdgeInsets.symmetric(vertical: 48),
        child: Center(
          child: SizedBox(
            width: 24,
            height: 24,
            child: CircularProgressIndicator(strokeWidth: 2, color: context.palette.action),
          ),
        ),
      );
}
