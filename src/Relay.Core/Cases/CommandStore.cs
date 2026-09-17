using System.Text.Json;
using Relay.Core.Storage;

namespace Relay.Core.Cases;

/// <summary>
/// Durable command outbox. Commands are claimed for exclusive dispatch via logical key or command id.
/// </summary>
public sealed class CommandStore
{
    private readonly DataRoot _root;
    private readonly object _gate = new();

    public CommandStore(DataRoot root) => _root = root;

    public string CommandsDirectory => _root.CommandsDirectory;
    public string CommandPath(string commandId) => Path.Combine(CommandsDirectory, commandId + ".json");
    public string LogicalIndexPath(string logicalKey)
    {
        var safe = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(logicalKey)));
        return Path.Combine(CommandsDirectory, "by-logical", safe + ".json");
    }

    public void Save(RuntimeCommand command)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(CommandsDirectory);
            AtomicFile.WriteAllText(CommandPath(command.CommandId), JsonSerializer.Serialize(command, RelayJson.Indented));
            if (!string.IsNullOrWhiteSpace(command.LogicalKey))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogicalIndexPath(command.LogicalKey))!);
                var index = new
                {
                    logicalKey = command.LogicalKey,
                    commandId = command.CommandId,
                    status = command.Status,
                    caseId = command.CaseId,
                };
                AtomicFile.WriteAllText(LogicalIndexPath(command.LogicalKey), JsonSerializer.Serialize(index, RelayJson.Indented));
            }
        }
    }

    public RuntimeCommand? TryLoad(string commandId)
    {
        var text = AtomicFile.ReadAllTextIfExists(CommandPath(commandId));
        return text is null ? null : JsonSerializer.Deserialize<RuntimeCommand>(text, RelayJson.Indented);
    }

    public RuntimeCommand? TryFindByLogicalKey(string logicalKey)
    {
        var text = AtomicFile.ReadAllTextIfExists(LogicalIndexPath(logicalKey));
        if (text is null) return null;
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("commandId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        return TryLoad(idEl.GetString()!);
    }

    /// <summary>
    /// Atomically claims a pending command for <paramref name="owner"/>.
    /// Returns false if already claimed/completed or another owner holds the logical key.
    /// </summary>
    public bool TryClaim(string commandId, string owner, DateTimeOffset now, out RuntimeCommand? command)
    {
        lock (_gate)
        {
            command = TryLoad(commandId);
            if (command is null) return false;
            if (command.Status is RuntimeCommandStatus.Completed or RuntimeCommandStatus.Cancelled or RuntimeCommandStatus.Failed)
                return false;
            if (command.Status == RuntimeCommandStatus.Claimed
                && !string.Equals(command.ClaimedBy, owner, StringComparison.Ordinal))
                return false;

            if (!string.IsNullOrWhiteSpace(command.LogicalKey))
            {
                var peer = TryFindByLogicalKey(command.LogicalKey);
                if (peer is not null
                    && peer.CommandId != command.CommandId
                    && peer.Status is RuntimeCommandStatus.Claimed or RuntimeCommandStatus.Completed)
                {
                    return false;
                }
            }

            command.Status = RuntimeCommandStatus.Claimed;
            command.ClaimedBy = owner;
            command.ClaimedAt = now;
            command.Attempt++;
            Save(command);
            return true;
        }
    }

    /// <summary>Claims by logical key so two cases cannot dispatch the same logical work.</summary>
    public bool TryClaimLogical(string logicalKey, string owner, DateTimeOffset now, out RuntimeCommand? command)
    {
        lock (_gate)
        {
            command = TryFindByLogicalKey(logicalKey);
            if (command is null) return false;
            return TryClaim(command.CommandId, owner, now, out command);
        }
    }

    public IReadOnlyList<RuntimeCommand> ListAll()
    {
        if (!Directory.Exists(CommandsDirectory)) return [];
        var list = new List<RuntimeCommand>();
        foreach (var file in Directory.GetFiles(CommandsDirectory, "*.json"))
        {
            var text = AtomicFile.ReadAllTextIfExists(file);
            if (text is null) continue;
            var cmd = JsonSerializer.Deserialize<RuntimeCommand>(text, RelayJson.Indented);
            if (cmd is not null) list.Add(cmd);
        }
        return list;
    }

    public IReadOnlyList<RuntimeCommand> ListPendingOrClaimed()
        => ListAll().Where(c => c.Status is RuntimeCommandStatus.Pending or RuntimeCommandStatus.Claimed).ToList();
}
