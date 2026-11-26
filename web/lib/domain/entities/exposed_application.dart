import 'package:equatable/equatable.dart';

/// Why Kintsugi is not the one fixing an exposed application. Mirrors `ExposureRoute`.
enum ExposureRoute { managedElsewhere, noUpgradePath }

/// How urgent it is, most urgent first. Mirrors `ExposureCriticality`.
enum ExposureCriticality { critical, high, unscored, medium, low }

/// An application with an open CVE that Kintsugi's own patching will not fix. Mirrors `ExposedApplicationDto`.
class ExposedApplication extends Equatable {
  const ExposedApplication({
    required this.applicationName,
    required this.route,
    required this.owner,
    required this.ownerIsAuto,
    required this.criticality,
    required this.worstCvssScore,
    required this.worstCveId,
    required this.cveCount,
    required this.knownExploited,
    required this.knownRansomwareUse,
    required this.hostCount,
  });

  final String applicationName;
  final ExposureRoute route;

  /// Who patches it, for [ExposureRoute.managedElsewhere].
  final String? owner;

  /// [owner] was set by Kintsugi and nobody has confirmed it.
  final bool ownerIsAuto;
  final ExposureCriticality criticality;
  final double? worstCvssScore;

  /// The CVE to quote to whoever fixes it: the exploited one first, then the highest score.
  final String? worstCveId;
  final int cveCount;
  final bool knownExploited;
  final bool knownRansomwareUse;
  final int hostCount;

  @override
  List<Object?> get props => [
        applicationName,
        route,
        owner,
        ownerIsAuto,
        criticality,
        worstCvssScore,
        worstCveId,
        cveCount,
        knownExploited,
        knownRansomwareUse,
        hostCount,
      ];
}
