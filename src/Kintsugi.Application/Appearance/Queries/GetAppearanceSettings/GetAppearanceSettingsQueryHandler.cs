using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Appearance.Queries.GetAppearanceSettings;

public class GetAppearanceSettingsQueryHandler : IRequestHandler<GetAppearanceSettingsQuery, AppearanceSettingsDto>
{
    private readonly IAppearanceSettingsRepository _repository;

    public GetAppearanceSettingsQueryHandler(IAppearanceSettingsRepository repository)
    {
        _repository = repository;
    }

    public async Task<AppearanceSettingsDto> Handle(GetAppearanceSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        // No row is not an error: it is every company that has never opened the screen, and what they
        // are told is exactly how the product looked before this existed.
        return new AppearanceSettingsDto(
            settings?.ColourMode ?? AppearanceSettings.DefaultColourMode,
            settings?.PresetKey ?? AppearanceSettings.DefaultPresetKey,
            settings?.AccentHex,
            settings?.Wallpaper);
    }
}
