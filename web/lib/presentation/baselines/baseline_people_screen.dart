import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_bloc/flutter_bloc.dart';
import 'package:go_router/go_router.dart';

import '../../core/di/locator.dart';
import '../../core/router/app_router.dart';
import '../../core/widgets/alert_box.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/kintsugi_table.dart';
import '../../core/widgets/page_scaffold.dart';
import '../../core/widgets/panel.dart';
import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/baseline.dart';
import '../../domain/entities/enums.dart';
import '../../domain/usecases/baseline_usecases.dart';
import 'baselines_bloc.dart';

/// A baseline's People view (`GET /api/admin/baselines/{id}/people`): who each host's signed-in Windows
/// sessions resolved to, how, and whether that person's signature is in place. Polls like the other
/// Baselines screens.
class BaselinePeopleScreen extends StatelessWidget {
  const BaselinePeopleScreen({super.key, required this.baselineId});

  final String baselineId;

  @override
  Widget build(BuildContext context) => BlocProvider(
        create: (_) => BaselinePeopleBloc(
          baselineId: baselineId,
          getBaselinePeople: locator<GetBaselinePeople>(),
        )..add(const BaselinesLoadRequested()),
        child: _BaselinePeopleView(baselineId: baselineId),
      );
}

class _BaselinePeopleView extends StatelessWidget {
  const _BaselinePeopleView({required this.baselineId});

  final String baselineId;

  @override
  Widget build(BuildContext context) =>
      BlocBuilder<BaselinePeopleBloc, BaselinesState<List<BaselinePersonRow>>>(
        builder: (context, state) {
          final rows = state.data;
          return PageScaffold(
            title: 'People',
            subtitle: 'Everyone signed in on this baseline\'s PCs, who the server matched them to, and '
                'whether their signature is in place. Matching is the server\'s: GCPW first, then the '
                'Windows username.',
            children: [
              if (state.error != null) AlertBox.error(state.error!),
              Align(
                alignment: Alignment.centerLeft,
                child: LinkText(label: '← Back to the baseline', onTap: () => context.go(Routes.baseline(baselineId))),
              ),
              const SizedBox(height: 12),
              if (rows == null)
                state.loading ? const Center(child: CircularProgressIndicator()) : const EmptyPanel('Not found.')
              else
                BaselinePeopleTable(rows: rows, baselineId: baselineId),
            ],
          );
        },
      );
}

/// The people rows as one [KintsugiTable]: the People view's, and the baseline host page's for one PC.
class BaselinePeopleTable extends StatelessWidget {
  const BaselinePeopleTable({super.key, required this.rows, required this.baselineId, this.showHost = true});

  final List<BaselinePersonRow> rows;
  final String baselineId;

  /// False on the host page, where every row is the same PC.
  final bool showHost;

  @override
  Widget build(BuildContext context) {
    if (rows.isEmpty) {
      return const EmptyPanel(
        'Nobody signed in has been reported yet. An agent lists its sessions when it next checks this '
        'baseline, within the hour.',
      );
    }

    // At most one outlook-signature item per version today, so the Item column only appears if that changes.
    final showItem = rows.map((r) => r.itemKey).toSet().length > 1;

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const HintText(
          'A person keeps counting for a PC for up to 30 days after they were last seen signed in there, '
          'so "Last Seen" is how to tell a current session from an old one.',
        ),
        const SizedBox(height: 8),
        KintsugiTable(
          minWidth: 1500,
          columns: [
            if (showHost) const TableColumnSpec(label: 'PC', width: FlexColumnWidth(1.1)),
            const TableColumnSpec(label: 'Windows User', width: FlexColumnWidth(1.1)),
            const TableColumnSpec(label: 'Email', width: FlexColumnWidth(1.6)),
            const TableColumnSpec(label: 'Resolved By', width: FlexColumnWidth(1.4)),
            const TableColumnSpec(label: 'Copy Rendered', width: FixedColumnWidth(150)),
            if (showItem) const TableColumnSpec(label: 'Item', width: FlexColumnWidth(1)),
            // Measured: "Not applicable" is a chip a Text cannot narrow (see KintsugiTable).
            TableColumnSpec(
              label: 'State',
              width: TableColumnSpec.forContent(
                BaselineItemState.values.map((s) => StatusChip.widthFor(context, s.label)).reduce(math.max),
              ),
            ),
            const TableColumnSpec(label: 'Observed', width: FlexColumnWidth(1.4)),
            const TableColumnSpec(label: 'Last Checked', width: FixedColumnWidth(150)),
            const TableColumnSpec(label: 'Last Seen', width: FixedColumnWidth(150)),
          ],
          rows: [
            for (final row in rows)
              KintsugiTableRow(
                cells: [
                  if (showHost)
                    LinkText(label: row.hostname, onTap: () => context.go(Routes.baselineHost(baselineId, row.hostId))),
                  Text(row.windowsUser),
                  row.email == null ? const NoValue() : Text(row.email!),
                  _resolvedBy(row),
                  row.copyRenderedAtUtc == null ? const HintText('No copy') : LocalTimestamp(row.copyRenderedAtUtc),
                  if (showItem) Text(row.itemKey),
                  row.state == null
                      ? const HintText('Not reported')
                      : StatusChip(row.state!.label, statusKey: row.state!.statusKey),
                  _observed(row),
                  LocalTimestamp(row.lastCheckedUtc),
                  LocalTimestamp(row.lastSeenUtc),
                ],
              ),
          ],
        ),
      ],
    );
  }

  static Widget _resolvedBy(BaselinePersonRow row) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(row.resolvedBy.label),
          if (row.reason != null && row.reason!.isNotEmpty) HintText(row.reason!),
        ],
      );

  static Widget _observed(BaselinePersonRow row) {
    if (row.observed == null && row.detail == null) return const NoValue();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (row.observed != null) CodeText(row.observed!, size: 11.5),
        if (row.detail != null) HintText(row.detail!),
      ],
    );
  }
}
