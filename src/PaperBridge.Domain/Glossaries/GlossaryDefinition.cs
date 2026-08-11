namespace PaperBridge.Domain.Glossaries;

public sealed record GlossaryDefinition
{
    public GlossaryDefinition(
        Guid id,
        string name,
        GlossarySource source,
        bool isEnabled = true,
        int priority = 0,
        string? topic = null,
        string? description = null,
        DateTimeOffset? updatedAtUtc = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Glossary identifier cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > 100)
        {
            throw new ArgumentException("Glossary name must not exceed 100 characters.", nameof(name));
        }
        Id = id;
        Name = name.Trim();
        Source = source;
        IsEnabled = isEnabled;
        Priority = priority;
        Topic = Normalize(topic);
        Description = Normalize(description);
        UpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow;
    }

    public Guid Id { get; }

    public string Name { get; }

    public GlossarySource Source { get; }

    public bool IsEnabled { get; }

    public int Priority { get; }

    public string? Topic { get; }

    public string? Description { get; }

    public DateTimeOffset UpdatedAtUtc { get; }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
