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
}
