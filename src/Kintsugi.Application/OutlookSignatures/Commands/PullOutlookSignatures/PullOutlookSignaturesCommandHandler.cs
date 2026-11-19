using System.Globalization;
using System.Text;
using MediatR;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Application.Common.Interfaces;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Application.OutlookSignatures.Commands.PullOutlookSignatures;

/// <summary>
/// Replaces the signature copies with what the source serves, all or nothing.
/// </summary>
/// <remarks>
/// <para>
/// Three outcomes, and the asymmetry between them is the design:
/// </para>
/// <list type="bullet">
/// <item><c>ok</c>: the whole set is replaced in one <c>SaveChangesAsync</c> — one transaction — upserting
/// each person and deleting anyone the source no longer lists.</item>
/// <item><c>disabled</c>: every copy is deleted. That is the source's kill switch: with no copy, the
/// agents leave each PC's signature files and defaults as they are.</item>
/// <item>Anything else — unreachable, a timeout, a 401, a body that is not the contract, or one with a
/// single entry over a cap — changes no copy and records why. A failed pull must never look like
/// <c>disabled</c>, or an outage at the source would strip everyone's signature status.</item>
/// </list>
/// <para>
/// The caps are checked over the whole response before anything is written, so a response is never half
/// applied: <see cref="OutlookSignatureRules.MaxEntries"/> entries, <see cref="PersonSignature.MaxBodyBytes"/>
/// per body, one valid and unique email per entry.
/// </para>
/// </remarks>
public class PullOutlookSignaturesCommandHandler : IRequestHandler<PullOutlookSignaturesCommand, OutlookSignaturePullResult?>
{
    private readonly IOutlookSignatureSourceSettingsRepository _settings;
    private readonly IPersonSignatureRepository _signatures;
    private readonly IOutlookSignatureSourceClient _client;
    private readonly IUnitOfWork _unitOfWork;

    public PullOutlookSignaturesCommandHandler(
        IOutlookSignatureSourceSettingsRepository settings,
        IPersonSignatureRepository signatures,
        IOutlookSignatureSourceClient client,
        IUnitOfWork unitOfWork)
    {
        _settings = settings;
        _signatures = signatures;
        _client = client;
        _unitOfWork = unitOfWork;
    }

    public async Task<OutlookSignaturePullResult?> Handle(PullOutlookSignaturesCommand request, CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken);
        if (settings is null || !settings.Enabled || !settings.IsConfigured)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var existing = await _signatures.GetAllAsync(cancellationToken);

        OutlookSignatureSourceResponse response;
        try
        {
            response = await _client.FetchAsync(settings.TokenAudience!, cancellationToken);
        }
        catch (ExternalServiceException ex)
        {
            return await Fail(settings, existing.Count, ex.Message, now, cancellationToken);
        }

        switch (response.Status)
        {
            case OutlookSignatureSourceResponse.StatusDisabled:
                _signatures.Remove(existing);
                settings.RecordPull(OutlookSignatureSourceSettings.StatusDisabled, 0, now);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return new OutlookSignaturePullResult(OutlookSignatureSourceSettings.StatusDisabled, 0, null);

            case OutlookSignatureSourceResponse.StatusOk:
                var (copies, problem) = Check(response.Signatures);
                if (problem is not null)
                {
                    return await Fail(settings, existing.Count, problem, now, cancellationToken);
                }

                var byEmail = existing.ToDictionary(s => s.Email, StringComparer.Ordinal);
                foreach (var copy in copies!)
                {
                    if (byEmail.Remove(copy.Email, out var row))
                    {
                        row.Replace(copy.Html, copy.Text, copy.RenderedAt, now);
                    }
                    else
                    {
                        await _signatures.AddAsync(PersonSignature.Create(copy.Email, copy.Html, copy.Text, copy.RenderedAt, now), cancellationToken);
                    }
                }

                // Whoever is left was not in this response: the source stopped serving them.
                _signatures.Remove(byEmail.Values);
                settings.RecordPull(OutlookSignatureSourceSettings.StatusOk, copies.Count, now);
                await _unitOfWork.SaveChangesAsync(cancellationToken);
                return new OutlookSignaturePullResult(OutlookSignatureSourceSettings.StatusOk, copies.Count, null);

            default:
                return await Fail(
                    settings, existing.Count,
                    "The source answered with a status that is neither 'ok' nor 'disabled'. Nothing was changed.", now, cancellationToken);
        }
    }

    private async Task<OutlookSignaturePullResult> Fail(
        OutlookSignatureSourceSettings settings, int held, string error, DateTimeOffset now, CancellationToken cancellationToken)
    {
        settings.RecordPullFailure(error, now);
        await _unitOfWork.SaveChangesAsync(cancellationToken);
        return new OutlookSignaturePullResult(OutlookSignatureSourceSettings.StatusError, held, settings.LastError);
    }

    private sealed record Copy(string Email, string Html, string Text, DateTimeOffset RenderedAt);

    /// <summary>Every check, over every entry, before anything is written. The message names the entry by
    /// position, never by its email: it is stored and shown, and need not carry a person's address.</summary>
    private static (IReadOnlyList<Copy>? Copies, string? Problem) Check(IReadOnlyList<SourceSignature?>? signatures)
    {
        const string Suffix = " Nothing was changed.";

        if (signatures is null)
        {
            return (null, "The source said 'ok' but sent no signatures list." + Suffix);
        }

        if (signatures.Count > OutlookSignatureRules.MaxEntries)
        {
            return (null, $"The source sent {signatures.Count} signatures, more than the {OutlookSignatureRules.MaxEntries} allowed." + Suffix);
        }

        var copies = new List<Copy>(signatures.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < signatures.Count; i++)
        {
            var entry = signatures[i];
            var where = $"Entry {i + 1}";

            if (entry is null || !OutlookSignatureRules.IsValidEmail(entry.Email))
            {
                return (null, $"{where} has no valid email." + Suffix);
            }

            var email = PersonSignature.NormalizeEmail(entry.Email!);
            if (!seen.Add(email))
            {
                return (null, $"{where} repeats an email already listed." + Suffix);
            }

            if (entry.Html is null || entry.Text is null)
            {
                return (null, $"{where} is missing its html or text." + Suffix);
            }

            if (Encoding.UTF8.GetByteCount(entry.Html) > PersonSignature.MaxBodyBytes
                || Encoding.UTF8.GetByteCount(entry.Text) > PersonSignature.MaxBodyBytes)
            {
                return (null, $"{where}'s html or text is over {PersonSignature.MaxBodyBytes / 1024} KiB." + Suffix);
            }

            if (!DateTimeOffset.TryParse(
                    entry.RenderedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var renderedAt))
            {
                return (null, $"{where} has no valid renderedAt." + Suffix);
            }

            copies.Add(new Copy(email, entry.Html, entry.Text, renderedAt));
        }

        return (copies, null);
    }
}
