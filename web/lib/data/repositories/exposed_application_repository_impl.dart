import '../../core/network/api_client.dart';
import '../../domain/entities/exposed_application.dart';
import '../../domain/repositories/repositories.dart';
import '../models/exposed_application_mapper.dart';

class ExposedApplicationRepositoryImpl implements ExposedApplicationRepository {
  const ExposedApplicationRepositoryImpl(this._api);

  final ApiClient _api;

  @override
  Future<List<ExposedApplication>> read() async =>
      exposedApplicationsFromJson(await _api.getJson('/api/admin/vulnerabilities/exposed') as Map<String, dynamic>);
}
