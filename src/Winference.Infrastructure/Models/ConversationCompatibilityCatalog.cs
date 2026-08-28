using System.Security.Cryptography;
using System.Text.Json;
using Winference.Core.Models;
using Winference.Core.Runtime;

namespace Winference.Infrastructure.Models;

public sealed record ConversationCompatibilityMember(
    string ModelFamilyId, string ProfileId, string ArtifactId, ExecutionMode ExecutionMode);

public sealed record ConversationCompatibilityGroup(
    ConversationCompatibility Compatibility, IReadOnlyList<ConversationCompatibilityMember> Members);

/// <summary>
/// Build-owned, explicitly reviewed equivalences. User registration metadata and
/// matching display names cannot create equivalence. An empty catalog is valid.
/// </summary>
public sealed class ConversationCompatibilityCatalog
{
    public const string PinnedSha256 = "717fd374b8ec34faa4f687bae1fe331e8337134f020c401e150024db1e88131e";
    private const int MaximumBytes = 64 * 1024;
    private const int MaximumGroups = 128;
    private readonly Dictionary<(string Family, string Profile, string Artifact, ExecutionMode Mode), ConversationCompatibility> _lookup;

    public IReadOnlyList<ConversationCompatibilityGroup> Groups { get; }

    private ConversationCompatibilityCatalog(IReadOnlyList<ConversationCompatibilityGroup> groups)
    {
        Groups = groups;
        _lookup = groups.SelectMany(group => group.Members.Select(member =>
            (Identity: (member.ModelFamilyId, member.ProfileId, member.ArtifactId, member.ExecutionMode), group.Compatibility)))
            .ToDictionary(item => item.Identity, item => item.Compatibility);
    }

    public ConversationCompatibility? For(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return _lookup.GetValueOrDefault((profile.ModelFamilyId, profile.ProfileId, profile.ArtifactId, profile.ExecutionMode));
    }

    public IReadOnlyList<ConversationCompatibilityMember> MembersFor(ConversationCompatibility compatibility, ExecutionMode mode)
    {
        ArgumentNullException.ThrowIfNull(compatibility);
        compatibility.Validate();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return Array.AsReadOnly(Groups.Where(group => group.Compatibility == compatibility)
            .SelectMany(group => group.Members).Where(member => member.ExecutionMode == mode).ToArray());
    }

    public bool AreCompatible(ModelProfile first, ModelProfile second) =>
        For(first) is { } key && key == For(second);

    /// <summary>
    /// Production uses the bundled path and pinned digest. A caller-specified
    /// digest exists for reviewed build catalog changes and isolated fixtures.
    /// </summary>
    public static ConversationCompatibilityCatalog Load(string path, string expectedSha256 = PinnedSha256)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The expected compatibility catalog digest is invalid.", nameof(expectedSha256));
        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[MaximumBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            if (total is 0 or > MaximumBytes) throw Invalid();
            bytes = buffer[..total];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeException("conversation_catalog_state",
                "The conversation compatibility catalog could not be read. Restore the complete Winference application folder.", error);
        }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            Keys(root, "schemaVersion", "groups");
            if (!root.GetProperty("schemaVersion").TryGetInt32(out var version) || version != 1) throw Invalid();
            var entries = root.GetProperty("groups");
            if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumGroups) throw Invalid();
            var groups = new List<ConversationCompatibilityGroup>();
            var keys = new HashSet<ConversationCompatibility>();
            var profiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries.EnumerateArray())
            {
                Keys(entry, "compatibility", "members");
                var value = entry.GetProperty("compatibility");
                Keys(value, "baseCheckpoint", "checkpointRevision", "tokenizerPolicy", "templatePolicy");
                var key = new ConversationCompatibility(Text(value, "baseCheckpoint"), Text(value, "checkpointRevision"),
                    Text(value, "tokenizerPolicy"), Text(value, "templatePolicy"));
                key.Validate();
                if (!keys.Add(key)) throw Invalid();
                var membersElement = entry.GetProperty("members");
                if (membersElement.ValueKind != JsonValueKind.Array || membersElement.GetArrayLength() is < 2 or > 3) throw Invalid();
                var modes = new HashSet<ExecutionMode>();
                var members = new List<ConversationCompatibilityMember>();
                foreach (var member in membersElement.EnumerateArray())
                {
                    Keys(member, "modelFamilyId", "profileId", "artifactId", "executionMode");
                    var modeText = Text(member, "executionMode");
                    if (!Enum.TryParse<ExecutionMode>(modeText, false, out var mode) || !Enum.IsDefined(mode) || modeText != mode.ToString() || !modes.Add(mode))
                        throw Invalid();
                    var model = new ConversationCompatibilityMember(Text(member, "modelFamilyId"), Text(member, "profileId"), Text(member, "artifactId"), mode);
                    if (!profiles.Add(model.ProfileId)) throw Invalid();
                    members.Add(model);
                }
                groups.Add(new(key, Array.AsReadOnly(members.ToArray())));
            }
            return new(Array.AsReadOnly(groups.ToArray()));
        }
        catch (RuntimeException) { throw; }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { throw Invalid(error); }
    }

    private static void Keys(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Invalid();
        if (seen.Count != allowed.Length) throw Invalid();
    }

    private static string Text(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text ||
            string.IsNullOrWhiteSpace(text) || text.Length > 512 || text != text.Trim() || text.Any(char.IsControl)) throw Invalid();
        return text;
    }

    private static RuntimeException Invalid(Exception? error = null) => new("conversation_catalog_integrity",
        "The conversation compatibility catalog differs from this build's approved mappings. Restore the complete Winference application folder.", error);
}
