using CodeLines.Core.Services;

namespace CodeLines.Tests;

public sealed class GitIgnoreRuleProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CodeLinesTests", Guid.NewGuid().ToString("N"));

    public GitIgnoreRuleProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "bin/\n*.tmp\n!keep.tmp\n");
        File.WriteAllText(Path.Combine(_root, "src", ".gitignore"), "/generated.cs\n");
    }

    [Fact]
    public void Applies_directory_glob_and_negation_rules()
    {
        var provider = new GitIgnoreRuleProvider();
        Assert.True(provider.IsIgnored(_root, Path.Combine(_root, "src", "bin"), true, [], []));
        Assert.True(provider.IsIgnored(_root, Path.Combine(_root, "cache.tmp"), false, [], []));
        Assert.False(provider.IsIgnored(_root, Path.Combine(_root, "keep.tmp"), false, [], []));
    }

    [Fact]
    public void Scopes_nested_gitignore_to_its_directory()
    {
        var provider = new GitIgnoreRuleProvider();
        Assert.True(provider.IsIgnored(_root, Path.Combine(_root, "src", "generated.cs"), false, [], []));
        Assert.False(provider.IsIgnored(_root, Path.Combine(_root, "generated.cs"), false, [], []));
    }

    [Fact]
    public void Custom_rules_are_evaluated_after_repository_rules()
    {
        var provider = new GitIgnoreRuleProvider();
        Assert.True(provider.IsIgnored(_root, Path.Combine(_root, "src", "secret.cs"), false, [], ["**/secret.cs"]));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}

