namespace Winference.Core.Models;

/// <summary>What the library can do with a profile right now. This is not a qualification claim.</summary>
public enum ModelAvailability
{
    Installed,
    DownloadRequired,
    ImportRequired,
    Unavailable
}

/// <summary>Availability plus a readable reason. Vendor support, local compatibility and observed results stay separate fields.</summary>
public sealed record ModelCompatibility(ModelAvailability State, string Summary, string? Reason = null)
{
    public bool IsInstalled => State == ModelAvailability.Installed;
    public bool CanDownload => State == ModelAvailability.DownloadRequired;
    public bool CanImport => State == ModelAvailability.ImportRequired;
    public string Label => Reason is { Length: > 0 } ? Reason : Summary;
}
