import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../domain/entities/host.dart';
import 'hosts_filters.dart';

/// The row above the table that holds the filters the administrator *chose*: a "+ Add filter" button,
/// one chip for each filter in use, "Clear all", and whatever [trailing] puts at the far end (the
/// import button).
///
/// There is no fixed set of dropdowns. The person picks the field they need (any of
/// [HostFilterField.values]), then one or more of its values; the pair becomes a chip they can edit
/// (press its text) or remove (its ×). Several values of one field are OR, several fields are AND.
/// The five two-option fields (Connection, Updates, CVEs, Department set, Location set) are ordinary
/// fields in that menu, with exactly their two values and counts.
class HostsFilterBar extends StatefulWidget {
  const HostsFilterBar({
    super.key,
    required this.filters,
    required this.hosts,
    required this.onSet,
    required this.onClear,
    required this.trailing,
  });

  final HostFilters filters;

  /// Every host, not just the ones the filters leave: the number beside each value says how many hosts
  /// have it, so choosing a value never makes the other values' numbers shrink.
  final List<HostSummary> hosts;

  /// Sets one field to exactly these values; an empty set removes its chip.
  final void Function(HostFilterField field, Set<String> values) onSet;
  final VoidCallback onClear;

  /// What sits at the right-hand end of the row.
  final Widget trailing;

  @override
  State<HostsFilterBar> createState() => _HostsFilterBarState();
}

class _HostsFilterBarState extends State<HostsFilterBar> {
  final _portal = OverlayPortalController();
  final _link = LayerLink();

  /// The field whose values the open menu is showing, or null while it is showing the list of fields.
  HostFilterField? _editing;
  Set<String> _draft = {};

  void _openFields() {
    setState(() {
      _editing = null;
      _portal.show();
    });
  }

  void _openValues(HostFilterField field) {
    setState(() {
      _editing = field;
      _draft = {...widget.filters.valuesOf(field)};
      _portal.show();
    });
  }

  void _close() => setState(_portal.hide);

  void _apply() {
    final field = _editing;
    if (field != null) widget.onSet(field, _draft);
    _close();
  }

  @override
  Widget build(BuildContext context) {
    final active = widget.filters.activeFields;

    return OverlayPortal(
      controller: _portal,
      overlayChildBuilder: (context) => _buildMenu(context),
      child: Wrap(
        spacing: 8,
        runSpacing: 8,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          CompositedTransformTarget(
            link: _link,
            child: PrimaryButton(
              key: const Key('add-filter'),
              label: '+ Add filter',
              onPressed: () => _portal.isShowing ? _close() : _openFields(),
            ),
          ),
          for (final field in active)
            _FilterChip(
              field: field,
              values: widget.filters.valuesOf(field),
              onEdit: () => _openValues(field),
              onRemove: () => widget.onSet(field, const {}),
            ),
          if (active.isNotEmpty) SecondaryButton(label: 'Clear all', onPressed: widget.onClear),
          widget.trailing,
        ],
      ),
    );
  }

  Widget _buildMenu(BuildContext context) {
    final palette = context.palette;
    final field = _editing;

    return Stack(
      children: [
        // The whole screen behind the menu, so a press anywhere else closes it.
        Positioned.fill(
          child: GestureDetector(behavior: HitTestBehavior.translucent, onTap: _close, child: const SizedBox.expand()),
        ),
        CompositedTransformFollower(
          link: _link,
          targetAnchor: Alignment.bottomLeft,
          followerAnchor: Alignment.topLeft,
          offset: const Offset(0, 6),
          child: Align(
            alignment: Alignment.topLeft,
            child: Material(
              color: palette.surface,
              elevation: 8,
              shape: RoundedRectangleBorder(
                borderRadius: BorderRadius.circular(6),
                side: BorderSide(color: palette.border),
              ),
              child: SizedBox(
                width: 300,
                child: field == null ? _buildFieldList(context) : _buildValueList(context, field),
              ),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildFieldList(BuildContext context) {
    final palette = context.palette;

    return Padding(
      padding: const EdgeInsets.all(6),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _MenuTitle('Filter by', palette: palette),
          for (final field in HostFilterField.values)
            InkWell(
              key: Key('filter-field-${field.name}'),
              onTap: () => _openValues(field),
              child: Padding(
                padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 9),
                child: Row(
                  children: [
                    Expanded(child: Text(field.label)),
                    if (widget.filters.isActive(field))
                      Text('${widget.filters.valuesOf(field).length} chosen', style: Theme.of(context).textTheme.bodySmall),
                  ],
                ),
              ),
            ),
        ],
      ),
    );
  }

  Widget _buildValueList(BuildContext context, HostFilterField field) {
    final palette = context.palette;
    final entries = countHostValues(field, widget.hosts);

    return Padding(
      padding: const EdgeInsets.all(6),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          _MenuTitle(field.label, palette: palette),
          ConstrainedBox(
            constraints: const BoxConstraints(maxHeight: 260),
            child: ListView(
              shrinkWrap: true,
              children: [
                for (final entry in entries)
                  InkWell(
                    key: Key('filter-value-${entry.key}'),
                    onTap: () => setState(
                      () => _draft.contains(entry.key) ? _draft.remove(entry.key) : _draft.add(entry.key),
                    ),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
                      child: Row(
                        children: [
                          Checkbox(
                            value: _draft.contains(entry.key),
                            onChanged: (_) => setState(
                              () => _draft.contains(entry.key) ? _draft.remove(entry.key) : _draft.add(entry.key),
                            ),
                            visualDensity: VisualDensity.compact,
                          ),
                          Expanded(child: Text(entry.key)),
                          Text('${entry.value}', style: Theme.of(context).textTheme.bodySmall),
                        ],
                      ),
                    ),
                  ),
              ],
            ),
          ),
          Padding(
            padding: const EdgeInsets.fromLTRB(6, 8, 6, 4),
            child: Row(
              mainAxisAlignment: MainAxisAlignment.end,
              children: [
                SecondaryButton(label: 'Back', onPressed: _openFields),
                const SizedBox(width: 8),
                PrimaryButton(key: const Key('filter-apply'), label: 'Apply', onPressed: _apply),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

class _MenuTitle extends StatelessWidget {
  const _MenuTitle(this.text, {required this.palette});

  final String text;
  final KintsugiPalette palette;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.fromLTRB(12, 8, 12, 6),
        child: Text(text.toUpperCase(), style: AppTheme.label(color: palette.muted, size: 10,)),
      );
}

class _FilterChip extends StatelessWidget {
  const _FilterChip({required this.field, required this.values, required this.onEdit, required this.onRemove});

  final HostFilterField field;
  final Set<String> values;
  final VoidCallback onEdit;
  final VoidCallback onRemove;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;
    final text = '${field.label}: ${(values.toList()..sort()).join(', ')}';

    return Container(
      key: Key('chip-${field.name}'),
      padding: const EdgeInsets.only(left: 12),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(99),
        border: Border.all(color: palette.action),
        color: palette.accentWash(0.07),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          MouseRegion(
            cursor: SystemMouseCursors.click,
            child: GestureDetector(
              key: Key('chip-edit-${field.name}'),
              onTap: onEdit,
              child: Text(text, style: TextStyle(color: palette.text, fontSize: 12.5, fontWeight: FontWeight.w600)),
            ),
          ),
          IconButton(
            key: Key('chip-remove-${field.name}'),
            tooltip: 'Remove this filter',
            visualDensity: VisualDensity.compact,
            iconSize: 15,
            onPressed: onRemove,
            icon: Icon(Icons.close, color: palette.muted),
          ),
        ],
      ),
    );
  }
}
