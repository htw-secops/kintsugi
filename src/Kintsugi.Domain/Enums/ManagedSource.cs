using System.Text.Json.Serialization;

namespace Kintsugi.Domain.Enums;

/// <summary>Who decided an application is patched by someone else.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ManagedSource
{
    /// <summary>An administrator marked it, or confirmed what Kintsugi had marked. The default.</summary>
    Admin,

    /// <summary>Kintsugi marked it from a fact it can see (a part of another product, a framework package the
    /// Store updates). Treated like <see cref="Admin"/> everywhere, but shown as automatic so a person can
    /// check it, and confirming or changing it makes it <see cref="Admin"/>.</summary>
    Auto
}
