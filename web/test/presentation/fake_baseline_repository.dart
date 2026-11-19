import 'package:kintsugi_web/domain/entities/baseline.dart';
import 'package:kintsugi_web/domain/repositories/repositories.dart';

/// An in-memory [BaselineRepository] for the Baselines screen tests. Every value is a placeholder:
/// this repository is public.
class FakeBaselineRepository implements BaselineRepository {
  FakeBaselineRepository({
    this.baselines = const [],
    this.detail,
    this.hostDetail,
    this.peopleRows = const [],
    this.applicationNames = const [],
  });

  List<BaselineSummary> baselines;
  BaselineDetail? detail;
  BaselineHostDetail? hostDetail;
  List<BaselinePersonRow> peopleRows;
  List<String> applicationNames;

  Map<String, dynamic>? created;
  final List<String> namesQueried = [];
  int peopleReads = 0;

  /// The last assignment saved, as the request body would carry it.
  Map<String, Object?>? assigned;

  @override
  Future<List<BaselineSummary>> list() async => baselines;

  @override
  Future<BaselineDetail> get(String baselineId) async => detail!;

  @override
  Future<BaselineHostDetail> host(String baselineId, String hostId) async => hostDetail!;

  @override
  Future<List<BaselinePersonRow>> people(String baselineId) async {
    peopleReads++;
    return peopleRows;
  }

  @override
  Future<List<String>> installedApplicationNames(String query) async {
    namesQueried.add(query);
    return applicationNames.where((n) => n.toLowerCase().contains(query.toLowerCase())).toList();
  }

  @override
  Future<void> create(Map<String, dynamic> body) async {
    created = body;
  }

  @override
  Future<void> addVersion(String baselineId, Map<String, dynamic> body) async {
    created = body;
  }

  @override
  Future<void> sign(String versionId, String documentSha256) async {}

  @override
  Future<void> assign(
    String baselineId, {
    required bool allWindowsHosts,
    required List<String> hostNames,
    String? applicationName,
  }) async {
    assigned = {'allWindowsHosts': allWindowsHosts, 'hostNames': hostNames, 'applicationName': applicationName};
  }
}
