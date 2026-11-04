using Winference.Core.Models;
using Winference.Infrastructure.Selection;

namespace Winference.Tests;

public class ModelCatalogTests
{
    private static LibraryFamily Family(string name, params LibraryProfile[] profiles) => new(new(name.ToLowerInvariant().Replace(' ', '-'), name, "Publisher", "About"), profiles);

    private static LibraryProfile Profile(string id, ExecutionMode mode, ModelAvailability state, string? path = null,
        long? download = 1000, bool import = false)
    {
        var family = new ModelFamily("f", "F", "P", "D");
        var artifact = new ModelArtifact(id, family, ModelArtifactFormat.Gguf, "Q4", "source", "1", 1, null, "runtime");
        return new(id, mode, "runtime", artifact, new(state, "summary"), download, 1000, path,
            state == ModelAvailability.Installed, state == ModelAvailability.DownloadRequired, import, string.Empty);
    }

    private static ModelLibrarySnapshot Snapshot(params LibraryFamily[] families) => new(families, "PC");

    [Fact]
    public void Models_this_PC_cannot_run_are_hidden()
    {
        var entries = ModelCatalogView.Build(Snapshot(
            Family("Runnable", Profile("a", ExecutionMode.NpuOnly, ModelAvailability.DownloadRequired)),
            Family("Too big", Profile("b", ExecutionMode.NpuOnly, ModelAvailability.Unavailable))), gpuSupported: true);

        Assert.Equal("Runnable", Assert.Single(entries).Family.DisplayName);
    }

    [Fact]
    public void A_broken_model_already_on_this_PC_stays_visible_so_it_can_be_repaired()
    {
        var entry = Assert.Single(ModelCatalogView.Build(Snapshot(
            Family("Moved", Profile("a", ExecutionMode.GpuOnly, ModelAvailability.Unavailable, path: @"C:\gone.gguf"))), gpuSupported: true));

        Assert.True(entry.IsOnThisPc);
        Assert.True(entry.NeedsRepair);
    }

    [Fact]
    public void The_folder_import_placeholder_is_not_listed_as_a_model()
    {
        Assert.Empty(ModelCatalogView.Build(Snapshot(
            Family("AMD export", Profile("a", ExecutionMode.GpuNpu, ModelAvailability.ImportRequired, import: true))), gpuSupported: true));
    }

    [Fact]
    public void Without_a_supported_GPU_only_GPU_downloads_disappear()
    {
        var entries = ModelCatalogView.Build(Snapshot(
            Family("Download", Profile("a", ExecutionMode.GpuOnly, ModelAvailability.DownloadRequired)),
            Family("Installed", Profile("b", ExecutionMode.GpuOnly, ModelAvailability.Installed, path: "x.gguf"))), gpuSupported: false);

        Assert.Equal("Installed", Assert.Single(entries).Family.DisplayName);
    }

    [Theory]
    [InlineData(ModelFilter.All, 3)]
    [InlineData(ModelFilter.Npu, 1)]
    [InlineData(ModelFilter.GpuNpu, 1)]
    [InlineData(ModelFilter.Gpu, 1)]
    public void The_filter_only_narrows_the_list(ModelFilter filter, int expected)
    {
        var entries = ModelCatalogView.Build(Snapshot(Family("One",
            Profile("a", ExecutionMode.NpuOnly, ModelAvailability.DownloadRequired),
            Profile("b", ExecutionMode.GpuNpu, ModelAvailability.Installed, "path"),
            Profile("c", ExecutionMode.GpuOnly, ModelAvailability.DownloadRequired))), true);

        Assert.Equal(expected, ModelCatalogView.Apply(entries, filter, null).Count);
    }

    [Fact]
    public void Installed_models_come_first_then_by_name()
    {
        var entries = ModelCatalogView.Build(Snapshot(
            Family("Alpha", Profile("a", ExecutionMode.NpuOnly, ModelAvailability.DownloadRequired)),
            Family("Zulu", Profile("b", ExecutionMode.NpuOnly, ModelAvailability.Installed, "path")),
            Family("Beta", Profile("c", ExecutionMode.NpuOnly, ModelAvailability.DownloadRequired))), true);

        Assert.Equal(["Zulu", "Alpha", "Beta"], ModelCatalogView.Apply(entries, ModelFilter.All, null).Select(entry => entry.Family.DisplayName));
    }

    [Fact]
    public void Search_matches_the_name_regardless_of_case()
    {
        var entries = ModelCatalogView.Build(Snapshot(
            Family("Phi Mini", Profile("a", ExecutionMode.NpuOnly, ModelAvailability.DownloadRequired)),
            Family("Llama", Profile("b", ExecutionMode.NpuOnly, ModelAvailability.DownloadRequired))), true);

        Assert.Equal("Phi Mini", Assert.Single(ModelCatalogView.Apply(entries, ModelFilter.All, " PHI ")).Family.DisplayName);
    }

    private static ModelCatalogEntry Entry(string id, ModelAvailability state = ModelAvailability.DownloadRequired,
        ExecutionMode mode = ExecutionMode.NpuOnly, long download = 1000) =>
        new(new ModelFamily(id.Replace(':', '-'), id, "P", "D"), Profile(id, mode, state, download: download));

    [Fact]
    public void A_model_already_here_is_recommended_over_any_download()
    {
        var picked = ModelRecommender.Pick([Entry("flm:phi4-mini-it:4b"), Entry("local", ModelAvailability.Installed)], 32L << 30);
        Assert.Equal("local", picked!.Profile.ProfileId);
    }

    [Fact]
    public void A_PC_with_plenty_of_memory_gets_the_larger_default()
    {
        var picked = ModelRecommender.Pick([Entry("flm:llama3.2:1b"), Entry("flm:phi4-mini-it:4b")], 24L << 30);
        Assert.Equal("flm:phi4-mini-it:4b", picked!.Profile.ProfileId);
    }

    [Fact]
    public void A_PC_with_less_memory_gets_a_small_model()
    {
        var picked = ModelRecommender.Pick([Entry("flm:phi4-mini-it:4b"), Entry("flm:llama3.2:1b")], 8L << 30);
        Assert.Equal("flm:llama3.2:1b", picked!.Profile.ProfileId);
    }

    [Fact]
    public void With_no_NPU_models_the_smallest_GPU_download_is_offered()
    {
        var picked = ModelRecommender.Pick([
            Entry("large", mode: ExecutionMode.GpuOnly, download: 9000),
            Entry("small", mode: ExecutionMode.GpuOnly, download: 100)], 8L << 30);
        Assert.Equal("small", picked!.Profile.ProfileId);
    }

    [Fact]
    public void An_unsupported_PC_gets_no_recommendation()
    {
        Assert.Null(ModelRecommender.Pick([], 8L << 30));
    }

    [Theory]
    [InlineData(1_000_000_000L, 2_000_000_000L, true)]
    [InlineData(1_000_000_000L, 1_000_000_000L, false)]
    [InlineData(0L, 0L, true)]
    public void Storage_check_leaves_room_for_verification(long needed, long free, bool expected)
    {
        Assert.Equal(expected, StorageCheck.HasRoom(needed, free));
    }

    [Theory]
    [InlineData(3_640_000_000L, "3.6 GB")]
    [InlineData(30_484_687L, "31 MB")]
    public void Sizes_read_naturally(long bytes, string expected)
    {
        Assert.Equal(expected, StorageCheck.Format(bytes));
    }
}

public class AppPreferencesStoreTests
{
    [Fact]
    public void Choices_survive_a_restart()
    {
        using var folder = new TemporaryFolder();
        Assert.True(new AppPreferencesStore(folder.Path).Write(new("Dark", ModelFilter.Npu)));

        Assert.Equal(new AppPreferences("Dark", ModelFilter.Npu), new AppPreferencesStore(folder.Path).Read());
    }
}
