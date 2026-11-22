using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Winference.Infrastructure.Gpu;

/// <summary>One chat turn sent to the GPU worker.</summary>
public sealed record GpuWorkerMessage(string Role, string Content);

/// <summary>
/// A request from the app. Op is "count", "generate", "cancel" or "stop". A cancel names the generate
/// request it ends by Id, so a late cancel can never stop the next reply.
/// </summary>
public sealed record GpuWorkerRequest(string Op, IReadOnlyList<GpuWorkerMessage>? Messages = null,
    int MaxTokens = 0, double Temperature = 0.7, bool UseRemaining = false, string? Id = null);

/// <summary>What the worker reports once the model is loaded on the GPU.</summary>
public sealed record GpuWorkerReady(int ProcessId, string EngineVersion, string Device, string DeviceDescription,
    long? DeviceMemoryBytes, int Layers, int ContextLength, int TrainedContextLength, string ModelDescription);

/// <summary>
/// A message from the worker. Type is "ready", "count", "text", "reasoning", "usage", "done" or "error".
/// </summary>
public sealed record GpuWorkerEvent(string Type, string? Text = null, int? Tokens = null,
    int? PromptTokens = null, int? CompletionTokens = null, double? TokensPerSecond = null,
    string? Finish = null, string? Code = null, string? Message = null, GpuWorkerReady? Ready = null,
    int? NewPromptTokens = null, double? PromptSeconds = null);

/// <summary>One JSON object per line, UTF-8, over the private pipe between the app and its GPU worker.</summary>
public static class GpuWorkerProtocol
{
    public const string WorkerFlag = "--gpu-worker";
    public const string PipePrefix = "winference-gpu-";
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T Deserialize<T>(string line) =>
        JsonSerializer.Deserialize<T>(line, Json) ?? throw new JsonException("Empty GPU worker message.");

    public static bool IsPipeName(string value) =>
        value.StartsWith(PipePrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(value[PipePrefix.Length..], "N", out _);
}
