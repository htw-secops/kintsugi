using MediatR;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.Appearance.Commands.UpdateAppearanceSettings;

public class UpdateAppearanceSettingsCommandHandler
    : IRequestHandler<UpdateAppearanceSettingsCommand, AppearanceSettingsDto>
{
    private readonly IAppearanceSettingsRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateAppearanceSettingsCommandHandler(IAppearanceSettingsRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<AppearanceSettingsDto> Handle(UpdateAppearanceSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetAsync(cancellationToken);

        if (settings is null)
        {
            settings = AppearanceSettings.Create(
                request.ColourMode, request.PresetKey, request.AccentHex, request.Wallpaper);
            await _repository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(request.ColourMode, request.PresetKey, request.AccentHex, request.Wallpaper);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        // The stored values, not the request's: the entity trims, lower-cases and upper-cases, and the
        // screen adopts what comes back.
        return new AppearanceSettingsDto(
            settings.ColourMode, settings.PresetKey, settings.AccentHex, settings.Wallpaper);
    }
}
