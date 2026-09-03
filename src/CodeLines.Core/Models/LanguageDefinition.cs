namespace CodeLines.Core.Models;

public sealed record LanguageDefinition(
    string Name,
    FileCategory Category,
    IReadOnlySet<string> Extensions,
    IReadOnlySet<string> FileNames,
    IReadOnlyList<string> LineCommentMarkers,
    IReadOnlyList<CommentPair> BlockCommentPairs);

public sealed record CommentPair(string Start, string End);

