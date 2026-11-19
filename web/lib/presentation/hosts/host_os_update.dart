import 'package:flutter/material.dart';

import '../../core/widgets/status_chip.dart';
import '../../core/widgets/text_bits.dart';
import '../../domain/entities/host.dart';

/// A host's operating-system update state: the same chip, latest-version text and "Restart Needed"
/// tag the table's OS Update column used to show. It lives in the details panel now (the table has
/// no room for the column), so it is a widget of its own rather than a private part of the screen.
class HostOsUpdate extends StatelessWidget {
  const HostOsUpdate({super.key, required this.host});

  final HostSummary host;

  @override
  Widget build(BuildContext context) {
    // A restart the host has asked for says more than "up to date": the updates are installed but
    // not all in effect yet. It replaces the chip when nothing else is pending, and sits under it
    // when an update is also waiting, so neither hides the other.
    final restart = host.restartRequired
        ? Tooltip(
            message: host.restartRequiredBecause == null
                ? 'Updates are installed. A restart finishes them.'
                : 'Updates are installed. A restart finishes them: ${host.restartRequiredBecause}',
            child: const StatusChip('Restart Needed', statusKey: 'update-available'),
          )
        : null;

    // Tri-state, and the third state is not a rounding of the other two: null means the host has
    // never reported an OS update check, which is different from being up to date.
    return switch (host.operatingSystemUpdateAvailable) {
      true => Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const StatusChip('Update Available', statusKey: 'update-available'),
            if (host.operatingSystemLatestVersion != null)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: HintText(host.operatingSystemLatestVersion!),
              ),
            if (restart != null) Padding(padding: const EdgeInsets.only(top: 4), child: restart),
          ],
        ),
      false => restart ?? const StatusChip('Up To Date', statusKey: 'up-to-date'),
      null => restart ?? const StatusChip('Not Checked', statusKey: 'unknown'),
    };
  }
}
