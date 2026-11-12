using System.Net;
using System.Text;
using Winference.Core.Discover;
using Winference.Core.Models;
using Winference.Infrastructure.Discover;
using Winference.Infrastructure.Selection;
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

    [Theory]
    [InlineData("Qwen3-0.6B", "Alibaba Qwen")]
    [InlineData("Llama-3.2-1B-Instruct", "Meta")]
    [InlineData("gpt-oss-20b", "OpenAI")]
    [InlineData("AMD-OLMo-1B-SFT-DPO-Hybrid", "AMD")]
    [InlineData("Unknown-Model-1B", null)]
    public void Models_are_credited_to_their_maker(string name, string? maker) => Assert.Equal(maker, ModelMakers.For(name));

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

    [Theory]
    [InlineData("Qwen3-0.6B-Q4_0.gguf", "Q4_0", true)]
    [InlineData("gemma-4-26B-UD-Q4_K_M.gguf", "UD-Q4_K_M", true)]
    [InlineData("Qwen3-0.6B-Q4_0_L.gguf", "Q4_0", false)]
    [InlineData("Qwen3-0.6B-IQ4_0.gguf", "Q4_0", false)]
    public void A_quantization_picks_its_own_file(string file, string quantization, bool expected) =>
        Assert.Equal(expected, HuggingFaceClient.MatchesQuantization(file, quantization));

    [Theory]
    [InlineData("model-Q4_K_M.gguf", true)]
    [InlineData("mmproj-model-f16.gguf", false)]
    [InlineData("model-Q4_K_M-00001-of-00002.gguf", false)]
    [InlineData("Q4_K_M/model-Q4_K_M.gguf", false)]
    public void Only_plain_single_files_are_downloaded(string file, bool expected) =>
        Assert.Equal(expected, HuggingFaceClient.IsSingleModelFile(file));

    [Fact]
    public async Task A_lookup_pins_the_commit_file_and_fingerprint()
    {
        const string commit = "50968a4468ef4233ed78cd7c3de230dd1d61a56b";
        const string hash = "33bcc57074ec7b6eada5a90651ee546ec0c2b271002c22baf9f1b2dd1e8f75cb";
        var client = new HuggingFaceClient(Serve(new()
        {
            ["Qwen3-0.6B-GGUF"] = $$$"""
            {"sha": "{{{commit}}}", "gated": false, "siblings": [
              {"rfilename": "README.md"},
              {"rfilename": "Qwen3-0.6B-Q4_0.gguf", "lfs": {"sha256": "{{{hash}}}", "size": 382156480}},
              {"rfilename": "Qwen3-0.6B-Q4_1.gguf", "lfs": {"sha256": "{{{hash}}}", "size": 409091776}}]}
            """
        }));

        var file = await client.ResolveGgufAsync("unsloth/Qwen3-0.6B-GGUF", null, "Q4_0");

        Assert.Equal("Qwen3-0.6B-Q4_0.gguf", file.FileName);
        Assert.Equal(commit, file.Revision);
        Assert.Equal(hash, file.Sha256);
        Assert.Equal(382156480, file.LengthBytes);
        Assert.Equal($"https://huggingface.co/unsloth/Qwen3-0.6B-GGUF/resolve/{commit}/Qwen3-0.6B-Q4_0.gguf", file.Download.ToString());
    }

    [Fact]
    public async Task Search_lists_public_gguf_repositories()
    {
        var client = new HuggingFaceClient(Serve(new()
        {
            ["models"] = """
            [{"id": "unsloth/Qwen3.5-4B-GGUF", "downloads": 1537690, "likes": 557},
             {"id": "someone/private-GGUF", "private": true},
             {"id": "not a repo"}]
            """
        }));

        var found = await client.SearchGgufAsync("qwen 4b");

        var repository = Assert.Single(found);
        Assert.Equal("unsloth", repository.Owner);
        Assert.Equal("Qwen3.5-4B-GGUF", repository.Name);
        Assert.Equal(1537690, repository.Downloads);
    }

    [Fact]
    public async Task A_gated_model_is_refused_before_any_download()
    {
        var client = new HuggingFaceClient(Serve(new() { ["M-GGUF"] = """{"sha": "50968a4468ef4233ed78cd7c3de230dd1d61a56b", "gated": "manual", "siblings": []}""" }));
        var error = await Assert.ThrowsAsync<Winference.Core.Runtime.RuntimeException>(() => client.ResolveGgufAsync("x/M-GGUF", "m.gguf", null));
        Assert.Contains("account", error.Message);
    }

    [Fact]
    public void Small_files_are_checked_with_their_git_fingerprint() =>
        // `git hash-object` of a file containing "hello\n".
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", HybridModelDownloadService.GitBlobSha1(Encoding.UTF8.GetBytes("hello\n")));

    [Fact]
    public void Measured_speed_follows_recent_replies_and_survives_a_restart()
    {
        var root = Directory.CreateTempSubdirectory("winference-speed-").FullName;
        try
        {
            var store = new ModelSpeedStore(root);
            store.Record("qwen3:0.6b", 80);
            store.Record("qwen3:0.6b", 100);
            store.Record("qwen3:0.6b", double.NaN);

            var reopened = new ModelSpeedStore(root).Read("qwen3:0.6b");

            Assert.NotNull(reopened);
            Assert.Equal(90, reopened.TokensPerSecond);
            Assert.Equal(2, reopened.Replies);
            Assert.Null(store.Read("never-used"));
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
