using Winference.Core.Models;

namespace Winference.Core.Gpu;

/// <summary>Header facts from an existing GGUF, not a hardware qualification or memory-fit promise.</summary>
public sealed record GgufModelMetadata(
    string DisplayName, string Architecture, string Quantization,
    int? MaximumContextLength, string TokenizerModel, string ChatTemplate,
    ulong TensorCount, uint Version);

/// <summary>References an externally owned file. Registering never copies, downloads or loads it.</summary>
public sealed record GpuModelRegistration(
    Guid Id, string ModelPath, long LengthBytes, string Sha256,
    GgufModelMetadata Metadata, ModelProfile Profile, DateTimeOffset RegisteredUtc,
    int SchemaVersion = 1)
{
    public string ModelId => "gguf:" + Sha256;
    public string DisplayName => Metadata.DisplayName;
    public string FilePath => ModelPath;
    public long FileBytes => LengthBytes;
    public string Architecture => Metadata.Architecture;
    public int ContextLength => Profile.ContextLength;
}

public sealed record GpuRuntimeInstallation(
    string RuntimeDirectory, string ExecutablePath, string ReleaseTag,
    string ArchiveSha256, string ManifestPath);

public sealed record GpuRuntimeStatus(
    bool IsInstalled, string Status, long DownloadBytes, GpuRuntimeInstallation? Installation);
