import '../../core/network/json_reader.dart';
import '../../domain/entities/exposed_application.dart';

/// Reads an `ExposedApplicationsDto`. An unknown route or criticality from a newer server reads as the
/// safer, louder value (no route, critical), so a new state is never shown as calmer than it is.
List<ExposedApplication> exposedApplicationsFromJson(Map<String, dynamic> json) =>
    listFromJson(json['applications'], (entry) {
      final route = switch (entry['route']) {
        'ManagedElsewhere' => ExposureRoute.managedElsewhere,
        _ => ExposureRoute.noUpgradePath,
      };
      return ExposedApplication(
        applicationName: entry['applicationName'] as String? ?? '',
        route: route,
        owner: entry['owner'] as String?,
        ownerIsAuto: entry['source'] == 'Auto',
        criticality: switch (entry['criticality']) {
          'High' => ExposureCriticality.high,
          'Unscored' => ExposureCriticality.unscored,
          'Medium' => ExposureCriticality.medium,
          'Low' => ExposureCriticality.low,
          _ => ExposureCriticality.critical,
        },
        worstCvssScore: (entry['worstCvssScore'] as num?)?.toDouble(),
        worstCveId: entry['worstCveId'] as String?,
        cveCount: (entry['cveCount'] as num?)?.toInt() ?? 0,
        knownExploited: entry['knownExploited'] as bool? ?? false,
        knownRansomwareUse: entry['knownRansomwareUse'] as bool? ?? false,
        hostCount: (entry['hostCount'] as num?)?.toInt() ?? 0,
      );
    });
