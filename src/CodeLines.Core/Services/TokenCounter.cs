using System.Collections.Concurrent;
using CodeLines.Core.Abstractions;
using Microsoft.ML.Tokenizers;

namespace CodeLines.Core.Services;

public sealed class TokenCounter : ITokenCounter
{
    private readonly ConcurrentDictionary<string, Tokenizer> _tokenizers = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> SupportedEncodings { get; } = ["o200k_base", "cl100k_base"];

    public long CountTokens(string text, string encodingName)
    {
        if (!SupportedEncodings.Contains(encodingName, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(encodingName), encodingName, "Unsupported tokenizer encoding.");
        var tokenizer = _tokenizers.GetOrAdd(encodingName, name => TiktokenTokenizer.CreateForEncoding(name));
        return tokenizer.CountTokens(text);
    }
}
