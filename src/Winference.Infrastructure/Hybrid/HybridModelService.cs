using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Winference.Core.Hybrid;
using Winference.Core.Models;
using Winference.Core.Runtime;
using Winference.Core.Setup;
using Winference.Infrastructure.Setup;

namespace Winference.Infrastructure.Hybrid;

/// <summary>
/// Registers an AMD Ryzen AI hybrid export in place. The folder describes the
/// artifact: every top-level file is hashed and <c>genai_config.json</c> is read
/// structurally. No runtime or model acquisition occurs here.
/// </summary>
public sealed class HybridModelService
{
    /// <summary>The largest conversation this build runs on the hybrid worker, reply included.</summary>
    public const int MaximumContextLength = 2048;
    public const int MaximumOutputTokens = 256;
    private const long MaximumRegistryBytes = 4L * 1024 * 1024;
    private const long MaximumExportBytes = 32L * 1024 * 1024 * 1024;
    private const int MaximumExportFiles = 96;
    private const int MaximumConfigurationBytes = 1024 * 1024;
    private const int MaximumIdentityBytes = 256 * 1024;
    private const int MaximumMetadataBytes = 16 * 1024;

    // AMD publishes below-chance MMLU scores for these exports and the weights
    // reproduce that in AMD's own server. They stay visible but unselectable.
    // Every other export is judged by its own folder and configuration.
    private static readonly string[] KnownDefectiveExports =
    [
        "qwen2.5-0.5b-instruct-onnx-ryzenai-1.7-hybrid",
        "qwen2.5-coder-0.5b-instruct-onnx-ryzenai-1.7-hybrid",
        "qwen2.5-0.5b-instruct_rai_1.7.1_hybrid"
    ];

    public static string ExecutionStatus(HybridModelRegistration model) => IsKnownDefective(model)
        ? "Warning: AMD reported low benchmark scores for this 0.5B export."
        : "Files verified; device readiness is checked when loaded.";
    public static bool CanExecute(HybridModelRegistration model) => true;

    private readonly string _registryPath;
    private readonly string _selectionPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HybridModelService(string dataRoot)
    {
        var directory = Path.Combine(Path.GetFullPath(dataRoot), "Hybrid");
        _registryPath = Path.Combine(directory, "models.json");
        _selectionPath = Path.Combine(directory, "selection.json");
    }
}
