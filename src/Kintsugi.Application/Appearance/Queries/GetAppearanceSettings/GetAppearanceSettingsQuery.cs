using MediatR;

namespace Kintsugi.Application.Appearance.Queries.GetAppearanceSettings;

public record GetAppearanceSettingsQuery : IRequest<AppearanceSettingsDto>;
