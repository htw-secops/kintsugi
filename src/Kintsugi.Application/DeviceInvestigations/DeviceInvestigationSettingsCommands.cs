using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.DeviceInvestigations;

/// <summary>The webhook is never returned: <see cref="HasSlackWebhook"/> stands in for it, as every secret in settings does.</summary>
public record DeviceInvestigationSettingsDto(bool Enabled, bool HasSlackWebhook, string? ConsoleBaseUrl);

/// <param name="Status">"WaitingForLog" or "Reported".</param>
public record DeviceInvestigationDto(
    Guid Id,
    DateTimeOffset OpenedUtc,
    string Why,
    string Status,
    DateTimeOffset? ReportedUtc,
    string? LikelyCause,
    string? Report,
    bool WrittenByAi,
    DateTimeOffset? AlertedUtc,
    string? AlertError)
{
    public static DeviceInvestigationDto From(DeviceInvestigation i) => new(
        i.Id, i.OpenedUtc, i.Why, i.ReportedUtc is null ? "WaitingForLog" : "Reported", i.ReportedUtc, i.LikelyCause, i.Report,
        i.WrittenByAi, i.AlertedUtc, i.AlertError);
}

public record GetDeviceInvestigationSettingsQuery : IRequest<DeviceInvestigationSettingsDto>;

/// <param name="SlackWebhookUrl">Blank keeps the stored one.</param>
public record UpdateDeviceInvestigationSettingsCommand(bool Enabled, string? SlackWebhookUrl, string? ConsoleBaseUrl, bool ClearSlackWebhook)
    : IRequest<DeviceInvestigationSettingsDto>;

/// <summary>Posts a test message to the stored webhook, so it is known to work before a device needs it.</summary>
public record SendTestDeviceInvestigationAlertCommand : IRequest;

public class DeviceInvestigationSettingsHandlers :
    IRequestHandler<GetDeviceInvestigationSettingsQuery, DeviceInvestigationSettingsDto>,
    IRequestHandler<UpdateDeviceInvestigationSettingsCommand, DeviceInvestigationSettingsDto>,
    IRequestHandler<SendTestDeviceInvestigationAlertCommand>
{
    private readonly IDeviceInvestigationRepository _repository;
    private readonly IChatWebhookClient _chat;
    private readonly IUnitOfWork _unitOfWork;

    public DeviceInvestigationSettingsHandlers(IDeviceInvestigationRepository repository, IChatWebhookClient chat, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _chat = chat;
        _unitOfWork = unitOfWork;
    }

    public async Task<DeviceInvestigationSettingsDto> Handle(GetDeviceInvestigationSettingsQuery request, CancellationToken cancellationToken) =>
        ToDto(await _repository.GetSettingsAsync(cancellationToken));

    public async Task<DeviceInvestigationSettingsDto> Handle(UpdateDeviceInvestigationSettingsCommand request, CancellationToken cancellationToken)
    {
        var settings = await _repository.GetSettingsAsync(cancellationToken);
        if (settings is null)
        {
            settings = DeviceInvestigationSettings.Create(request.Enabled, request.SlackWebhookUrl, request.ConsoleBaseUrl);
            await _repository.AddSettingsAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(request.Enabled, request.SlackWebhookUrl, request.ConsoleBaseUrl, request.ClearSlackWebhook);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(settings);
    }

    public async Task Handle(SendTestDeviceInvestigationAlertCommand request, CancellationToken cancellationToken)
    {
        var url = (await _repository.GetSettingsAsync(cancellationToken))?.SlackWebhookUrl
            ?? throw new ConflictException("Save a Slack webhook first.");
        try
        {
            await _chat.PostAsync(url, "Kintsugi test: reports on unstable devices will arrive in this channel.", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ExternalServiceException)
        {
            throw new ExternalServiceException($"The webhook did not accept the message: {ex.Message}");
        }
    }

    private static DeviceInvestigationSettingsDto ToDto(DeviceInvestigationSettings? s) =>
        new(s?.Enabled ?? false, s?.SlackWebhookUrl is not null, s?.ConsoleBaseUrl);
}
