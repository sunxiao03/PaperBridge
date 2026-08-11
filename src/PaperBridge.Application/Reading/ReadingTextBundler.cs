using System.Text;

namespace PaperBridge.Application.Reading;

public static class ReadingTextBundler
{
    public const int DefaultMaximumBundleCharacters = 12_000;
    public const int MaximumBundles = 96;

    public static IReadOnlyList<string> Bundle(
        IEnumerable<DocumentTextChunk> chunks,
        int maximumCharacters = DefaultMaximumBundleCharacters)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        if (maximumCharacters is < 1_000 or > 50_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        }

        var result = new List<string>();
        var current = new StringBuilder();
        foreach (var chunk in chunks)
        {
            var value = $"[Page {chunk.PageIndex + 1}; Section {chunk.SectionTitle}]\n{chunk.Text}";
            var offset = 0;
            while (offset < value.Length)
            {
                var available = maximumCharacters - current.Length;
                if (available < 200 && current.Length > 0)
                {
                    Flush();
                    available = maximumCharacters;
                }

                var length = Math.Min(available, value.Length - offset);
                current.Append(value, offset, length);
                offset += length;
                if (offset < value.Length)
                {
                    Flush();
                }
                else
                {
                    current.AppendLine().AppendLine();
                }
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            if (result.Count >= MaximumBundles)
            {
                throw new InvalidOperationException($"总结分块超过 {MaximumBundles} 个请求单元的上限。");
            }

            result.Add(current.ToString().Trim());
            current.Clear();
        }
    }

    public static IReadOnlyList<string> BundleText(
        IEnumerable<string> items,
        int maximumCharacters = 30_000)
    {
        ArgumentNullException.ThrowIfNull(items);
        var synthetic = items.Select((text, index) => new DocumentTextChunk(
            $"summary-{index}", index, "partial summary", 0, text));
        return Bundle(synthetic, maximumCharacters);
    }
}
