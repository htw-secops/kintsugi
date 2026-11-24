import '../entities/appearance.dart';
import '../repositories/repositories.dart';

class GetAppearanceSettings {
  const GetAppearanceSettings(this._repository);

  final AppearanceSettingsRepository _repository;

  Future<AppearanceSettings> call() => _repository.read();
}

class UpdateAppearanceSettings {
  const UpdateAppearanceSettings(this._repository);

  final AppearanceSettingsRepository _repository;

  Future<AppearanceSettings> call(AppearanceSettings settings) => _repository.update(settings);
}
