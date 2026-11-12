using System.Net;
using System.Text;
using Winference.Core.Discover;
using Winference.Core.Models;
using Winference.Infrastructure.Discover;
using Winference.Infrastructure.Setup;

namespace Winference.Tests;

public class DiscoverTests
{
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "config", "catalogs"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return directory.FullName;
    }

    private static string BundledFlmList => Path.Combine(Repository(), "config", "catalogs", "flm-1.0.12.json");
    private static string BundledFlmInfo => Path.Combine(Repository(), "config", "catalogs", "flm-1.0.12-model-info.json");
    private static string BundledLemonade => Path.Combine(Repository(), "external", "lemonade", "2026.46.2", "resources", "server_models.json");

    [Theory]
    [InlineData("Llama-3.2-1B-NPU2")]
    [InlineData("Llama-3.2-1B-Instruct-GGUF")]
    [InlineData("Llama-3.2-1B-Instruct-Hybrid")]
    public void Builds_of_one_model_share_a_family_key(string name) => Assert.Equal("llama321b", ModelFamilyKey.For(name));

    [Fact]
    public void Catalog_labels_become_capabilities() =>
        Assert.Equal(ModelCapabilities.Reasoning | ModelCapabilities.Vision | ModelCapabilities.Tools,
            ModelCapabilityLabels.FromLabels(["chat", "reasoning", "vision", "tool-calling", "hot"]));

    [Theory]
    [InlineData(2_000_000_000L, FitLevel.FitsWell)]
    [InlineData(12_000_000_000L, FitLevel.Tight)]
    [InlineData(20_000_000_000L, FitLevel.WontFit)]
    public void Fit_compares_a_model_with_the_memory_left_after_windows(long size, FitLevel expected) =>
        Assert.Equal(expected, ModelFit.Estimate(size, 24L * 1024 * 1024 * 1024));

    [Fact]
    public void Registry_keeps_chat_models_our_engines_can_run()
    {
        var models = LemonadeRegistry.Parse(Encoding.UTF8.GetBytes("""
        {
          "File-GGUF": {"checkpoint": "unsloth/A-GGUF:A-Q4_K_M.gguf", "recipe": "llamacpp", "labels": ["chat"], "size": 1.5},
          "Quant-GGUF": {"checkpoint": "unsloth/B-GGUF:Q4_0", "recipe": "llamacpp", "labels": ["reasoning"], "size": 0.5, "suggested": true},
          "Scope-GGUF": {"checkpoint": "x/C-GGUF:C.gguf", "recipe": "llamacpp", "labels": ["chat"], "source": "modelscope"},
          "Embed-GGUF": {"checkpoint": "x/D-GGUF:D.gguf", "recipe": "llamacpp", "labels": ["embeddings"]},
          "E-Hybrid": {"checkpoint": "amd/E-onnx-ryzenai-1.7-hybrid", "recipe": "ryzenai-llm", "labels": ["chat"], "size": 2},
          "F-Hybrid": {"checkpoint": "amd/F-onnx-ryzenai-1.8-hybrid", "recipe": "ryzenai-llm", "labels": ["chat"]},
          "G-NPU": {"checkpoint": "amd/G-onnx-ryzenai-1.7-npu", "recipe": "ryzenai-llm", "labels": ["chat"]}
        }
        """));

        Assert.Equal(["File-GGUF", "Quant-GGUF", "E-Hybrid"], models.Select(model => model.Name));
        Assert.Equal("A-Q4_K_M.gguf", models[0].FileName);
        Assert.Equal("Q4_0", models[1].Quantization);
        Assert.True(models[1].Suggested);
        Assert.Equal(ExecutionMode.GpuNpu, models[2].Mode);
        Assert.Equal(2_000_000_000, models[2].SizeBytes);
    }

    [Fact]
    public void The_bundled_registry_offers_tested_gpu_and_gpu_npu_models()
    {
        var models = LemonadeRegistry.Load(BundledLemonade);
        Assert.Contains(models, model => model.Mode == ExecutionMode.GpuOnly);
        Assert.Contains(models, model => model.Mode == ExecutionMode.GpuNpu);
        Assert.All(models, model => Assert.True(LemonadeRegistry.IsRepository(model.Repository)));
    }

    [Fact]
    public async Task A_refresh_is_used_only_while_its_files_are_unchanged()
    {
        var root = Directory.CreateTempSubdirectory("winference-refresh-").FullName;
        var app = Directory.CreateTempSubdirectory("winference-app-").FullName;
        try
        {
            CopyBundled(app);
            // Upstream changed since this build, so the hashes differ from the pinned bundled pair.
            var list = File.ReadAllText(BundledFlmList) + "\n";
            var service = new CatalogRefreshService(root, app, Serve(new()
            {
                ["model_list.json"] = list,
                ["model_info.json"] = File.ReadAllText(BundledFlmInfo),
                ["server_models.json"] = File.ReadAllText(BundledLemonade),
            }));

            var result = await service.RefreshAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(3, result.UpdatedFiles);
            var (catalog, info) = CatalogRefreshService.ResolveFlm(root, app);
            Assert.StartsWith(service.Directory, catalog);
            Assert.NotEmpty(FlmModelCatalog.Load(catalog, info).Models);
            Assert.False(service.IsStale(TimeSpan.FromDays(1)));

            File.AppendAllText(catalog, " ");
            Assert.Equal(service.BundledPath(CatalogRefreshService.FlmList), CatalogRefreshService.ResolveFlm(root, app).Catalog);
        }
        finally { Directory.Delete(root, true); Directory.Delete(app, true); }
    }

    [Fact]
    public async Task A_broken_upstream_list_keeps_the_previous_copy()
    {
        var root = Directory.CreateTempSubdirectory("winference-refresh-").FullName;
        var app = Directory.CreateTempSubdirectory("winference-app-").FullName;
        try
        {
            CopyBundled(app);
            var service = new CatalogRefreshService(root, app, Serve(new()
            {
                ["model_list.json"] = "<html>not json</html>",
                ["model_info.json"] = File.ReadAllText(BundledFlmInfo),
                ["server_models.json"] = File.ReadAllText(BundledLemonade),
            }));

            var result = await service.RefreshAsync();

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.UpdatedFiles);
            Assert.NotNull(result.Problem);
            Assert.Equal(service.BundledPath(CatalogRefreshService.FlmList), service.PathFor(CatalogRefreshService.FlmList));
            Assert.StartsWith(service.Directory, service.PathFor(CatalogRefreshService.LemonadeModels));
        }
        finally { Directory.Delete(root, true); Directory.Delete(app, true); }
    }

    [Fact]
    public async Task Without_a_connection_the_bundled_lists_stay_in_use()
    {
        var root = Directory.CreateTempSubdirectory("winference-refresh-").FullName;
        try
        {
            var service = new CatalogRefreshService(root, Repository(), Serve([]));
            var result = await service.RefreshAsync();
            Assert.False(result.Succeeded);
            Assert.True(service.IsStale(TimeSpan.FromDays(1)));
        }
        finally { Directory.Delete(root, true); }
    }

    private static void CopyBundled(string app)
    {
        Directory.CreateDirectory(Path.Combine(app, "Catalogs"));
        Directory.CreateDirectory(Path.Combine(app, "Runtimes", "Lemonade", "resources"));
        File.Copy(BundledFlmList, Path.Combine(app, "Catalogs", "catalog.json"));
        File.Copy(BundledFlmInfo, Path.Combine(app, "Catalogs", "files.json"));
        File.Copy(BundledLemonade, Path.Combine(app, "Runtimes", "Lemonade", "resources", "server_models.json"));
    }

    /// <summary>Answers by file name; anything else is a 404, like a failed fetch.</summary>
    private static HttpClient Serve(Dictionary<string, string> files) => new(new FileHandler(files));

    private sealed class FileHandler(Dictionary<string, string> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(Path.GetFileName(request.RequestUri!.AbsolutePath), out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
