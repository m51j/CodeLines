using CodeLines.Core.AiUsage.Agents.Adapters;

namespace CodeLines.Tests.AiUsage.Adapters;

public sealed class AgentAdaptersTests : IDisposable
{
    private readonly AdapterTestEnv _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void ListsAdaptersInCcstatsTabOrder() =>
        Assert.Equal(["codex", "antigravity-ide", "antigravity", "agy", "zcode", "opencode", "kilo", "cline", "copilot", "hermes", "lmstudio"],
            AgentAdapters.Create(_t.Env).Select(a => a.Id));

    [Fact]
    public async Task NothingIsDetectedOrLoadedOnAnEmptyMachine()
    {
        foreach (var adapter in AgentAdapters.Create(_t.Env))
        {
            Assert.False(adapter.Detect(), adapter.Id);
            Assert.Empty(await adapter.LoadAsync(false));
        }
    }
}
