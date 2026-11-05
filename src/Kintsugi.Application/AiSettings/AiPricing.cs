namespace Kintsugi.Application.AiSettings;

/// <summary>Turns tokens into an estimated cost using the prices in the models.dev catalogue.</summary>
public static class AiPricing
{
    /// <summary>
    /// The estimated cost in US dollars, or null when the catalogue holds no price for the model.
    /// </summary>
    /// <remarks>
    /// The model is matched by id across every provider, ignoring a Vertex-style <c>@version</c>
    /// suffix, because a connection stores the id it calls (<c>claude-sonnet-5-5</c>) while the
    /// catalogue lists the same model under several providers (<c>claude-sonnet-5-5@default</c>).
    /// Where several providers price it, the first with a price wins; they are normally the same.
    /// </remarks>
    public static decimal? Estimate(AiCatalogDto catalog, string model, long inputTokens, long outputTokens)
    {
        var wanted = Normalise(model);

        foreach (var provider in catalog.Providers)
        {
            foreach (var entry in provider.Models)
            {
                if (entry.InputPerMillion is null || entry.OutputPerMillion is null
                    || !string.Equals(Normalise(entry.Id), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return (inputTokens * entry.InputPerMillion.Value + outputTokens * entry.OutputPerMillion.Value) / 1_000_000m;
            }
        }

        return null;
    }

    private static string Normalise(string model)
    {
        var id = model.Trim();
        var at = id.IndexOf('@');
        return at < 0 ? id : id[..at];
    }
}
