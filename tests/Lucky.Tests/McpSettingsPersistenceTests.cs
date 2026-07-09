using Lucky.Core;

namespace Lucky.Tests;

public sealed class McpSettingsPersistenceTests
{
    [Fact]
    public async Task SaveAsync_RoundTripsBrowserAndMcpSettingsWithSafeDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "lucky-state.json");
            var store = new LuckyStore(path);
            var state = await store.LoadAsync();
            state.Settings.Browser.Enabled = true;
            state.Settings.Browser.AllowedDomains = [".Docs.Example.Test", "docs.example.test", "invalid domain"];
            state.Settings.Browser.MaxPageChars = 999999;
            state.Settings.Mcp.Enabled = true;
            state.Settings.Mcp.RequestTimeoutSeconds = 0;
            state.Settings.Mcp.MaxToolOutputChars = 0;
            state.Settings.Mcp.Servers =
            [
                new McpServerDefinition
                {
                    Id = "catalog",
                    Name = "Catalog",
                    Command = "npx",
                    Arguments = ["-y", "server-package", "bad\nargument"]
                }
            ];
            state.Settings.Sandbox = new CodeExecutionSandboxSettings
            {
                Enabled = true,
                Image = "local/sandbox:latest",
                AllowReadOnlyProjectMount = true,
                TimeoutSeconds = 999,
                MemoryMiB = 9999,
                CpuLimit = 9,
                PidsLimit = 0,
                ScratchMiB = 0
            };

            await store.SaveAsync(state);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("\"Command\"", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("server-package", persisted, StringComparison.Ordinal);
            Assert.Contains("EncryptedLaunchConfiguration", persisted, StringComparison.Ordinal);
            var loaded = await new LuckyStore(path).LoadAsync();

            Assert.True(loaded.Settings.Browser.Enabled);
            Assert.Equal(["docs.example.test"], loaded.Settings.Browser.AllowedDomains);
            Assert.Equal(40000, loaded.Settings.Browser.MaxPageChars);
            Assert.True(loaded.Settings.Mcp.Enabled);
            Assert.Equal(60, loaded.Settings.Mcp.RequestTimeoutSeconds);
            Assert.Equal(16000, loaded.Settings.Mcp.MaxToolOutputChars);
            var server = Assert.Single(loaded.Settings.Mcp.Servers);
            Assert.Equal("npx", server.Command);
            Assert.Equal(["-y", "server-package"], server.Arguments);
            Assert.Equal(McpTransportKind.Stdio, server.Transport);
            Assert.True(loaded.Settings.Sandbox.Enabled);
            Assert.Equal("local/sandbox:latest", loaded.Settings.Sandbox.Image);
            Assert.False(loaded.Settings.Sandbox.AllowReadOnlyProjectMount);
            Assert.Equal(120, loaded.Settings.Sandbox.TimeoutSeconds);
            Assert.Equal(2048, loaded.Settings.Sandbox.MemoryMiB);
            Assert.Equal(2, loaded.Settings.Sandbox.CpuLimit);
            Assert.Equal(128, loaded.Settings.Sandbox.PidsLimit);
            Assert.Equal(128, loaded.Settings.Sandbox.ScratchMiB);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyPlainTextMcpConfigurationOnNextSave()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "lucky-state.json");
            await File.WriteAllTextAsync(path, """
            {
              "Settings": {
                "Mcp": {
                  "Enabled": true,
                  "Servers": [
                    {
                      "Id": "legacy",
                      "Name": "Legacy server",
                      "Transport": 0,
                      "Command": "node",
                      "Arguments": ["server.mjs", "--token", "legacy-secret-token"],
                      "WorkingDirectory": "C:\\tools",
                      "Enabled": true
                    }
                  ]
                }
              }
            }
            """);

            var store = new LuckyStore(path);
            var state = await store.LoadAsync();
            var loadedServer = Assert.Single(state.Settings.Mcp.Servers);
            Assert.Equal("node", loadedServer.Command);
            Assert.Equal(["server.mjs", "--token", "legacy-secret-token"], loadedServer.Arguments);
            Assert.Equal("C:\\tools", loadedServer.WorkingDirectory);

            await store.SaveAsync(state);
            var persisted = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("legacy-secret-token", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Command\"", persisted, StringComparison.Ordinal);
            Assert.Contains("EncryptedLaunchConfiguration", persisted, StringComparison.Ordinal);

            var reloaded = await new LuckyStore(path).LoadAsync();
            var reloadedServer = Assert.Single(reloaded.Settings.Mcp.Servers);
            Assert.Equal("node", reloadedServer.Command);
            Assert.Equal(["server.mjs", "--token", "legacy-secret-token"], reloadedServer.Arguments);
            Assert.Equal("C:\\tools", reloadedServer.WorkingDirectory);
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
