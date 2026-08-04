namespace Winference.Core.Models;

/// <summary>The file/package format of one immutable model artifact.</summary>
public enum ModelArtifactFormat
{
    Gguf,
    RyzenAiHybrid,
    FastFlowLm
}

/// <summary>One execution-mode build. A family name alone never identifies an artifact.</summary>
public sealed record ModelArtifact(
    string Id, ModelFamily Family, ModelArtifactFormat Format, string Quantization,
    string Source, string Revision, long? LengthBytes, string? Sha256, string Runtime)
{
    public string FormatLabel => Format switch
    {
        ModelArtifactFormat.Gguf => "GGUF",
        ModelArtifactFormat.RyzenAiHybrid => "AMD Ryzen AI export",
        ModelArtifactFormat.FastFlowLm => "FastFlowLM package",
        _ => "Unknown"
    };

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentNullException.ThrowIfNull(Family);
        Family.Validate();
        if (!Enum.IsDefined(Format)) throw new ArgumentOutOfRangeException(nameof(Format));
        if (Quantization.Length > 80) throw new ArgumentException("A quantization label is too long.", nameof(Quantization));
        if (Source.Length > 2_000 || Revision.Length > 200) throw new ArgumentException("The artifact source identity is too long.", nameof(Source));
        if (LengthBytes is < 0 or > 200L * 1024 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(LengthBytes));
        if (Sha256 is not null && (Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("An artifact SHA-256 must contain 64 hexadecimal characters.", nameof(Sha256));
        if (string.IsNullOrWhiteSpace(Runtime) || Runtime.Length > 120)
            throw new ArgumentException("An artifact must name its runtime.", nameof(Runtime));
    }
}
