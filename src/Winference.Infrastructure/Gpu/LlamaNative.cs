// Bindings for llama.cpp v1.0.0 (build b12359): include/llama.h and ggml/include/ggml-backend.h.
// Struct layouts must match that header exactly. C bool is one byte, so it is byte here.
using System.Runtime.InteropServices;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Gpu;

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaModelParams
{
    public IntPtr Devices;
    public IntPtr TensorBufferOverrides;
    public int GpuLayers;
    public int SplitMode;
    public int LoadMode;
    public int LazyMode;
    public int MainGpu;
    public IntPtr TensorSplit;
    public IntPtr ProgressCallback;
    public IntPtr ProgressCallbackUserData;
    public IntPtr KvOverrides;
    public byte VocabOnly;
    public byte CheckTensors;
    public byte UseExtraBufferTypes;
    public byte NoHost;
    public byte NoAlloc;
    public byte LoadMtp;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaContextParams
{
    public uint ContextLength;
    public uint BatchSize;
    public uint MicroBatchSize;
    public uint MaxSequences;
    public uint RecurrentSnapshots;
    public uint MaxOutputs;
    public uint MaxOutputsPerSequence;
    public int Threads;
    public int BatchThreads;
    public int ContextType;
    public int RopeScalingType;
    public int PoolingType;
    public int AttentionType;
    public int FlashAttentionType;
    public float RopeFrequencyBase;
    public float RopeFrequencyScale;
    public float YarnExtensionFactor;
    public float YarnAttentionFactor;
    public float YarnBetaFast;
    public float YarnBetaSlow;
    public uint YarnOriginalContext;
    public IntPtr EvalCallback;
    public IntPtr EvalCallbackUserData;
    public int KeyCacheType;
    public int ValueCacheType;
    public nuint MoeCacheSize;
    public float MoeCacheCpuShare;
    public IntPtr AbortCallback;
    public IntPtr AbortCallbackData;
    public byte Embeddings;
    public byte OffloadKqv;
    public byte NoPerf;
    public byte OpOffload;
    public byte SwaFull;
    public byte KvUnified;
    public IntPtr Samplers;
    public nuint SamplerCount;
    public IntPtr OtherContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LlamaChatMessage
{
    public IntPtr Role;
    public IntPtr Content;
}

/// <summary>
/// The llama.cpp functions Winference calls. Each one is looked up by name in the pinned
/// llama.dll, ggml.dll or ggml-base.dll, so a missing export fails at load, not mid-chat.
/// </summary>
internal static unsafe class LlamaNative
{
    public const int DeviceTypeGpu = 1;
    public const int DeviceTypeIntegratedGpu = 2;
    public const int SplitModeNone = 0;
    public const int LogLevelWarn = 3;
    public const uint DefaultSeed = 0xFFFFFFFF;

    // ggml (device registry and logging)
    public static delegate* unmanaged[Cdecl]<byte*, void> BackendLoadAllFromPath;
    public static delegate* unmanaged[Cdecl]<nuint> DeviceCount;
    public static delegate* unmanaged[Cdecl]<nuint, IntPtr> DeviceGet;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> DeviceName;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> DeviceDescription;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> DeviceType;
    public static delegate* unmanaged[Cdecl]<IntPtr, nuint*, nuint*, void> DeviceMemory;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> DeviceBackendRegistry;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*> RegistryName;

    // llama
    public static delegate* unmanaged[Cdecl]<byte*> Version;
    public static delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, byte*, void*, void>, void*, void> LogSet;
    public static delegate* unmanaged[Cdecl]<void> BackendInit;
    public static delegate* unmanaged[Cdecl]<void> BackendFree;
    public static delegate* unmanaged[Cdecl]<LlamaModelParams> ModelDefaultParams;
    public static delegate* unmanaged[Cdecl]<LlamaContextParams> ContextDefaultParams;
    public static delegate* unmanaged[Cdecl]<byte*, LlamaModelParams, IntPtr> ModelLoadFromFile;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> ModelFree;
    public static delegate* unmanaged[Cdecl]<IntPtr, LlamaContextParams, IntPtr> InitFromModel;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> Free;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint> ContextSize;
    public static delegate* unmanaged[Cdecl]<IntPtr, uint> BatchSize;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> GetMemory;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> ModelGetVocab;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> ModelLayerCount;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> ModelTrainedContext;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*> ModelChatTemplate;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, int> ModelDescription;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte, void> MemoryClear;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int, int, byte> MemorySequenceRemove;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int> MemorySequencePositionMax;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr> BatchInit;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> BatchFree;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> BatchClear;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int, int> BatchAddToken;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, int*, byte> BatchSetPosition;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, byte, byte> BatchSetOutputLogits;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int> Process;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, byte> VocabIsEndOfGeneration;
    public static delegate* unmanaged[Cdecl]<IntPtr, int> VocabBos;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte> VocabAddsBos;
    public static delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int*, int, byte, byte, int> Tokenize;
    public static delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, int, byte, int> TokenToPiece;
    public static delegate* unmanaged[Cdecl]<byte*, LlamaChatMessage*, nuint, byte, byte*, int, int> ChatApplyTemplate;
    public static delegate* unmanaged[Cdecl]<byte, IntPtr> SamplerChainInit;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void> SamplerChainAdd;
    public static delegate* unmanaged[Cdecl]<int, IntPtr> SamplerTopK;
    public static delegate* unmanaged[Cdecl]<float, nuint, IntPtr> SamplerTopP;
    public static delegate* unmanaged[Cdecl]<float, nuint, IntPtr> SamplerMinP;
    public static delegate* unmanaged[Cdecl]<float, IntPtr> SamplerTemperature;
    public static delegate* unmanaged[Cdecl]<uint, IntPtr> SamplerDistribution;
    public static delegate* unmanaged[Cdecl]<IntPtr> SamplerGreedy;
    public static delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int> SamplerSample;
    public static delegate* unmanaged[Cdecl]<IntPtr, void> SamplerFree;

    private static readonly List<IntPtr> Libraries = [];

    /// <summary>Loads the pinned libraries from the verified runtime folder only, then binds every function.</summary>
    public static void Load(string runtimeDirectory)
    {
        if (Libraries.Count > 0) return;
        // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_DEFAULT_DIRS: dependencies resolve
        // beside each library and in System32, never from the current directory or PATH.
        foreach (var name in new[] { "ggml-base.dll", "ggml.dll", "llama.dll" })
        {
            var handle = LoadLibraryEx(Path.Combine(runtimeDirectory, name), IntPtr.Zero, 0x1100);
            if (handle == IntPtr.Zero)
                throw new RuntimeException("gpu_native_load", "The GPU engine could not be loaded. Repair the GPU component from Models.");
            Libraries.Add(handle);
        }
        BackendLoadAllFromPath = (delegate* unmanaged[Cdecl]<byte*, void>)Export("ggml_backend_load_all_from_path");
        DeviceCount = (delegate* unmanaged[Cdecl]<nuint>)Export("ggml_backend_dev_count");
        DeviceGet = (delegate* unmanaged[Cdecl]<nuint, IntPtr>)Export("ggml_backend_dev_get");
        DeviceName = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Export("ggml_backend_dev_name");
        DeviceDescription = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Export("ggml_backend_dev_description");
        DeviceType = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("ggml_backend_dev_type");
        DeviceMemory = (delegate* unmanaged[Cdecl]<IntPtr, nuint*, nuint*, void>)Export("ggml_backend_dev_memory");
        DeviceBackendRegistry = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Export("ggml_backend_dev_backend_reg");
        RegistryName = (delegate* unmanaged[Cdecl]<IntPtr, byte*>)Export("ggml_backend_reg_name");

        Version = (delegate* unmanaged[Cdecl]<byte*>)Export("llama_version");
        LogSet = (delegate* unmanaged[Cdecl]<delegate* unmanaged[Cdecl]<int, byte*, void*, void>, void*, void>)Export("llama_log_set");
        BackendInit = (delegate* unmanaged[Cdecl]<void>)Export("llama_backend_init");
        BackendFree = (delegate* unmanaged[Cdecl]<void>)Export("llama_backend_free");
        ModelDefaultParams = (delegate* unmanaged[Cdecl]<LlamaModelParams>)Export("llama_model_default_params");
        ContextDefaultParams = (delegate* unmanaged[Cdecl]<LlamaContextParams>)Export("llama_context_default_params");
        ModelLoadFromFile = (delegate* unmanaged[Cdecl]<byte*, LlamaModelParams, IntPtr>)Export("llama_model_load_from_file");
        ModelFree = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("llama_model_free");
        InitFromModel = (delegate* unmanaged[Cdecl]<IntPtr, LlamaContextParams, IntPtr>)Export("llama_init_from_model");
        Free = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("llama_free");
        ContextSize = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Export("llama_n_ctx");
        BatchSize = (delegate* unmanaged[Cdecl]<IntPtr, uint>)Export("llama_n_batch");
        GetMemory = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Export("llama_get_memory");
        ModelGetVocab = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Export("llama_model_get_vocab");
        ModelLayerCount = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("llama_model_n_layer");
        ModelTrainedContext = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("llama_model_n_ctx_train");
        ModelChatTemplate = (delegate* unmanaged[Cdecl]<IntPtr, byte*, byte*>)Export("llama_model_chat_template");
        ModelDescription = (delegate* unmanaged[Cdecl]<IntPtr, byte*, nuint, int>)Export("llama_model_desc");
        MemoryClear = (delegate* unmanaged[Cdecl]<IntPtr, byte, void>)Export("llama_memory_clear");
        MemorySequenceRemove = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int, byte>)Export("llama_memory_seq_rm");
        MemorySequencePositionMax = (delegate* unmanaged[Cdecl]<IntPtr, int, int>)Export("llama_memory_seq_pos_max");
        BatchInit = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)Export("llama_batch_ext_init");
        BatchFree = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("llama_batch_ext_free");
        BatchClear = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("llama_batch_ext_clear");
        BatchAddToken = (delegate* unmanaged[Cdecl]<IntPtr, int, int, int>)Export("llama_batch_ext_add_token");
        BatchSetPosition = (delegate* unmanaged[Cdecl]<IntPtr, int, int*, byte>)Export("llama_batch_ext_set_pos");
        BatchSetOutputLogits = (delegate* unmanaged[Cdecl]<IntPtr, int, byte, byte>)Export("llama_batch_ext_set_output_logits");
        Process = (delegate* unmanaged[Cdecl]<IntPtr, int, IntPtr, int>)Export("llama_process");
        VocabIsEndOfGeneration = (delegate* unmanaged[Cdecl]<IntPtr, int, byte>)Export("llama_vocab_is_eog");
        VocabBos = (delegate* unmanaged[Cdecl]<IntPtr, int>)Export("llama_vocab_bos");
        VocabAddsBos = (delegate* unmanaged[Cdecl]<IntPtr, byte>)Export("llama_vocab_get_add_bos");
        Tokenize = (delegate* unmanaged[Cdecl]<IntPtr, byte*, int, int*, int, byte, byte, int>)Export("llama_tokenize");
        TokenToPiece = (delegate* unmanaged[Cdecl]<IntPtr, int, byte*, int, int, byte, int>)Export("llama_token_to_piece");
        ChatApplyTemplate = (delegate* unmanaged[Cdecl]<byte*, LlamaChatMessage*, nuint, byte, byte*, int, int>)Export("llama_chat_apply_template");
        SamplerChainInit = (delegate* unmanaged[Cdecl]<byte, IntPtr>)Export("llama_sampler_chain_init");
        SamplerChainAdd = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)Export("llama_sampler_chain_add");
        SamplerTopK = (delegate* unmanaged[Cdecl]<int, IntPtr>)Export("llama_sampler_init_top_k");
        SamplerTopP = (delegate* unmanaged[Cdecl]<float, nuint, IntPtr>)Export("llama_sampler_init_top_p");
        SamplerMinP = (delegate* unmanaged[Cdecl]<float, nuint, IntPtr>)Export("llama_sampler_init_min_p");
        SamplerTemperature = (delegate* unmanaged[Cdecl]<float, IntPtr>)Export("llama_sampler_init_temp");
        SamplerDistribution = (delegate* unmanaged[Cdecl]<uint, IntPtr>)Export("llama_sampler_init_dist");
        SamplerGreedy = (delegate* unmanaged[Cdecl]<IntPtr>)Export("llama_sampler_init_greedy");
        SamplerSample = (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int, int>)Export("llama_sampler_sample");
        SamplerFree = (delegate* unmanaged[Cdecl]<IntPtr, void>)Export("llama_sampler_free");
    }

    public static string? Text(byte* value) => value is null ? null : Marshal.PtrToStringUTF8((IntPtr)value);

    private static IntPtr Export(string name)
    {
        foreach (var library in Libraries)
            if (NativeLibrary.TryGetExport(library, name, out var address)) return address;
        throw new RuntimeException("gpu_native_identity", "The GPU engine is missing a function this build needs. Repair the GPU component from Models.");
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string name, IntPtr file, uint flags);
}
