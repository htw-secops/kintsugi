import 'dart:async';

import 'package:flutter/material.dart';

import '../../core/theme/app_theme.dart';
import '../../core/theme/kintsugi_palette.dart';
import '../../core/widgets/buttons.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/host.dart';
import 'hosts_format.dart';

/// Which row the mouse is over, shared by every cell of a table so the hostname cell can show its copy
/// icon for *the row*, not only for the cell.
///
/// A short grace period before it clears, because the cells of a [Table] are separated by padding no
/// region covers, and without it the icon would blink off as the pointer crossed a gutter on its way
/// to it.
class HostRowHover extends ChangeNotifier {
  String? _hovered;
  Timer? _clear;

  String? get hovered => _hovered;

  void enter(String id) {
    _clear?.cancel();
    if (_hovered == id) return;
    _hovered = id;
    notifyListeners();
  }

  void exit(String id) {
    _clear?.cancel();
    _clear = Timer(const Duration(milliseconds: 120), () {
      if (_hovered != id) return;
      _hovered = null;
      notifyListeners();
    });
  }

  @override
  void dispose() {
    _clear?.cancel();
    super.dispose();
  }
}

/// Wraps one cell so hovering it marks its row hovered.
class HostRowHoverRegion extends StatelessWidget {
  const HostRowHoverRegion({super.key, required this.hostId, required this.hover, required this.child});

  final String hostId;
  final HostRowHover hover;
  final Widget child;

  @override
  Widget build(BuildContext context) => MouseRegion(
        onEnter: (_) => hover.enter(hostId),
        onExit: (_) => hover.exit(hostId),
        child: child,
      );
}

/// The Hostname cell: the name (a link that opens the details panel), a small copy icon that appears
/// when the row is hovered, and a hover card with the host's identity.
///
/// **The card stays open while the mouse is on it**, which is the whole point: it carries copy icons
/// (hostname, serial) and an icon is no use if the card closes on the way to it. It closes a moment
/// after the pointer has left both the name and the card.
class HostnameCell extends StatefulWidget {
  const HostnameCell({super.key, required this.host, required this.hover, required this.onOpen});

  final HostSummary host;
  final HostRowHover hover;

  /// Opens the details panel.
  final VoidCallback onOpen;

  @override
  State<HostnameCell> createState() => _HostnameCellState();
}

class _HostnameCellState extends State<HostnameCell> {
  final _portal = OverlayPortalController();
  final _link = LayerLink();
  Timer? _hide;

  void _show() {
    _hide?.cancel();
    if (!_portal.isShowing) _portal.show();
  }

  void _scheduleHide() {
    _hide?.cancel();
    _hide = Timer(const Duration(milliseconds: 180), () {
      if (mounted && _portal.isShowing) _portal.hide();
    });
  }

  @override
  void dispose() {
    _hide?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final host = widget.host;

    return OverlayPortal(
      controller: _portal,
      overlayChildBuilder: (context) => CompositedTransformFollower(
        link: _link,
        targetAnchor: Alignment.bottomLeft,
        followerAnchor: Alignment.topLeft,
        offset: const Offset(0, 6),
        child: Align(
          alignment: Alignment.topLeft,
          child: MouseRegion(
            onEnter: (_) => _show(),
            onExit: (_) => _scheduleHide(),
            child: HostHoverCard(host: host),
          ),
        ),
      ),
      child: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          Flexible(
            child: CompositedTransformTarget(
              link: _link,
              child: MouseRegion(
                onEnter: (_) => _show(),
                onExit: (_) => _scheduleHide(),
                child: LinkText(label: host.hostname, onTap: widget.onOpen, dense: true),
              ),
            ),
          ),
          // A fixed slot, so the copy icon appearing never moves the name or the cells after it.
          SizedBox(
            width: 30,
            child: ListenableBuilder(
              listenable: widget.hover,
              builder: (context, _) => widget.hover.hovered == host.id
                  ? _CopyIcon(
                      key: Key('copy-hostname-${host.id}'),
                      tooltip: 'Copy hostname',
                      onPressed: () => copyHostValue(context, host.hostname),
                    )
                  : const SizedBox.shrink(),
            ),
          ),
        ],
      ),
    );
  }
}

class _CopyIcon extends StatelessWidget {
  const _CopyIcon({super.key, required this.tooltip, required this.onPressed});

  final String tooltip;
  final VoidCallback onPressed;

  @override
  Widget build(BuildContext context) => IconButton(
        tooltip: tooltip,
        onPressed: onPressed,
        iconSize: 15,
        color: context.palette.muted,
        hoverColor: context.palette.accentWash(0.12),
        constraints: const BoxConstraints(minWidth: 28, minHeight: 28),
        padding: EdgeInsets.zero,
        visualDensity: VisualDensity.compact,
        icon: const Icon(Icons.copy_outlined),
      );
}

/// The card shown when the mouse rests on a hostname: who and where the host is.
///
/// Hostname and serial have a copy icon, for pasting into Snipe-IT and elsewhere. The assigned
/// person is the **short name only** ("Alex M.") and has none: the full name and the email are behind
/// Reveal in the details panel, where the server records that they were looked at.
class HostHoverCard extends StatelessWidget {
  const HostHoverCard({super.key, required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) {
    final palette = context.palette;

    Widget row(String label, String? value, {bool copy = false, Key? copyKey}) => Container(
          padding: const EdgeInsets.symmetric(vertical: 3),
          decoration: BoxDecoration(border: Border(top: BorderSide(color: palette.border))),
          child: Row(
            children: [
              SizedBox(width: 96, child: Text(label, style: Theme.of(context).textTheme.bodySmall)),
              Expanded(
                child: value == null
                    ? const HintText('Not set yet')
                    : Text(value, overflow: TextOverflow.ellipsis),
              ),
              if (copy && value != null)
                _CopyIcon(key: copyKey, tooltip: 'Copy ${label.toLowerCase()}', onPressed: () => copyHostValue(context, value))
              else
                const SizedBox(width: 28),
            ],
          ),
        );

    return Material(
      key: Key('host-card-${host.id}'),
      color: palette.backgroundAlt,
      elevation: 8,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: BorderSide(color: palette.border),
      ),
      child: SizedBox(
        width: 340,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(14, 12, 10, 10),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(host.hostname, style: AppTheme.display(color: palette.neonSoft, size: 12.5, letterSpacing: 0.6)),
              const SizedBox(height: 6),
              row('Hostname', host.hostname, copy: true, copyKey: Key('card-copy-hostname-${host.id}')),
              row('Serial', host.serialNumber, copy: true, copyKey: Key('card-copy-serial-${host.id}')),
              // The address is here because the table has no column for it. Left out when the host has not
              // reported one, rather than saying "Not set yet" about something nobody sets.
              if (host.ipAddress != null) row('IP address', host.ipAddress),
              row('Assigned to', host.assignedShortName),
              row('Department', host.department),
              row('Location', host.location),
              const SizedBox(height: 8),
              const HintText('Copy icons paste into Snipe-IT and elsewhere. Click the hostname for more details (admins only).'),
            ],
          ),
        ),
      ),
    );
  }
}
