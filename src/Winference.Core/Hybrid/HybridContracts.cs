using Winference.Core.Models;

namespace Winference.Core.Hybrid;

/// <summary>One immutable file from a reviewed hybrid export.</summary>
public sealed record HybridModelFile(string Name, long LengthBytes, string Sha256);

/// <summary>References an external model folder. Registration does not copy, download or load weights.</summary>
public sealed record HybridModelRegistration(
    Guid Id, string ModelDirectory, string Sha256, string DisplayName,
    string RepositoryId, string Revision, IReadOnlyList<HybridModelFile> ModelFiles,
    ModelProfile Profile, DateTimeOffset RegisteredUtc, int SchemaVersion = 1, string ModelType = "")
{
    public string ModelId => "hybrid:" + Sha256;
    public int ContextLength => Profile.ContextLength;
    public long LengthBytes => ModelFiles.Sum(file => file.LengthBytes);
}
