namespace Relay.Core.Artifacts;

/// <summary>Exact connector identity. Display as <c>{Id}@{Version}</c>.</summary>
public readonly record struct ConnectorRef(string Id, int Version)
{
    public string Display => $"{Id}@{Version}";
    public override string ToString() => Display;
}

/// <summary>Exact connector action identity.</summary>
public readonly record struct ConnectorActionRef(
    string ConnectorId,
    int ConnectorVersion,
    string ActionId,
    int ActionVersion)
{
    public string Display => $"{ConnectorId}@{ConnectorVersion}/{ActionId}@{ActionVersion}";
    public override string ToString() => Display;
    public ConnectorRef Connector => new(ConnectorId, ConnectorVersion);
}

/// <summary>Exact judgment definition identity. Display as <c>{Id}@{Version}</c>.</summary>
public readonly record struct JudgmentDefinitionRef(string Id, int Version)
{
    public string Display => $"{Id}@{Version}";
    public override string ToString() => Display;
}

/// <summary>Exact Reflex identity. Display as <c>{Id}@{Version}</c>.</summary>
public readonly record struct ReflexRef(string Id, int Version)
{
    public string Display => $"{Id}@{Version}";
    public override string ToString() => Display;
}
