using System.Text.Json.Nodes;

namespace Storloom.Core.Chats;

/// <summary>A captured identity, not a claim about changes between observations.</summary>
public sealed record ChatIdentity(string Sender, string Conversation, string? Nickname = null,
    string? FriendRemark = null, string? GroupCard = null, string? RecordedName = null,
    string? Avatar = null, bool MessageNickname = false, bool MessageGroupCard = false,
    DateTimeOffset? ObservedAt = null) {
    [System.Text.Json.Serialization.JsonIgnore]
    public string Version => JsonFiles.Hash(new { Sender, Conversation, Nickname, FriendRemark, GroupCard, RecordedName, Avatar, MessageNickname, MessageGroupCard });
}

internal static class ChatIdentityHistory {
    // Legacy messages have no per-message identity. Freeze the maps before any later repair/update.
    public static void FreezeLegacy(JsonObject index) {
        if (index["identityBaseline"] != null) return;
        index["identityBaseline"] = new JsonObject {
            ["contactNames"] = index["contactNames"]?.DeepClone() ?? new JsonObject(),
            ["avatars"] = index["avatars"]?.DeepClone() ?? new JsonObject(),
            ["observedAt"] = DateTimeOffset.UtcNow.ToString("O")
        };
    }
}
