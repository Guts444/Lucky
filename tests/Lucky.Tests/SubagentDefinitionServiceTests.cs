using Lucky.Core;

namespace Lucky.Tests;

public sealed class SubagentDefinitionServiceTests
{
    [Fact]
    public async Task LoadAsync_LoadsProjectCustomAgentsAndKeepsBuiltIns()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.SubagentDefinitionServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".lucky", "agents"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".lucky", "agents", "api-reviewer.json"), """
            {
              "name": "API Reviewer",
              "description": "Reviews provider payloads.",
              "instructions": "Focus on provider compatibility.",
              "tools": [ "project_search", "project_read_file", "unknown_tool" ],
              "accessLevel": "FullAccess",
              "autoActivate": true
            }
            """);
            var state = new LuckyState();
            var project = new LuckyProject { Name = "Test", Path = root };

            var definitions = await new SubagentDefinitionService().LoadAsync(state, project);

            Assert.Contains(definitions, definition => definition.Name == "explorer");
            var custom = Assert.Single(definitions, definition => definition.Name == "api-reviewer");
            Assert.Equal(["project_search", "project_read_file"], custom.Tools);
            Assert.Equal(HarnessAccessLevel.FullAccess, custom.AccessLevel);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
