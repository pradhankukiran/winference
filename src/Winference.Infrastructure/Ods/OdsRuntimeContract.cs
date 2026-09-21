namespace Winference.Infrastructure.Ods;

/// <summary>Private bridge evidence, checked again against retained Windows ownership.</summary>
internal sealed record OdsReadyRecord
{
    public int SchemaVersion { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public int SupervisorId { get; init; }
    public int ProcessId { get; init; }
    public string StartedUtc { get; init; } = string.Empty;
    public string ExecutablePath { get; init; } = string.Empty;
    public int Port { get; init; }
    public string ModelId { get; init; } = string.Empty;
    public string ModelPath { get; init; } = string.Empty;
    public string ModelArgumentPath { get; init; } = string.Empty;
    public string ModelSha256 { get; init; } = string.Empty;
    public long ModelLengthBytes { get; init; }
    public int ContextSize { get; init; }
    public int RuntimeContext { get; init; }
    public string ReleaseTag { get; init; } = string.Empty;
    public int Build { get; init; }
    public string Commit { get; init; } = string.Empty;
    public string Device { get; init; } = string.Empty;
    public string DeviceDescription { get; init; } = string.Empty;
    public string AdapterName { get; init; } = string.Empty;
    public string AdapterPnpId { get; init; } = string.Empty;
    public string DriverVersion { get; init; } = string.Empty;
    public int OffloadedLayers { get; init; }
    public int TotalLayers { get; init; }
}
