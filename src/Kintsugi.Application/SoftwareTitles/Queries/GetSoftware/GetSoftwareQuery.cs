using MediatR;

namespace Kintsugi.Application.SoftwareTitles.Queries.GetSoftware;

/// <summary>The whole Software screen: every title with its counts and script state, and every secret
/// without its value.</summary>
public record GetSoftwareQuery : IRequest<SoftwareScreenDto>;
