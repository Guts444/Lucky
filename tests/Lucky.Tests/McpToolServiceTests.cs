using Lucky.Core;

namespace Lucky.Tests;

public sealed class McpToolServiceTests
{
    [Fact]
    public async Task OpenSessionAsync_DiscoversAndExecutesAStdioTool()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"lucky-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "test-mcp.ps1");
        await File.WriteAllTextAsync(scriptPath, TestServerScript);

        try
        {
            var service = new McpToolService();
            var settings = new McpSettings
            {
                Enabled = true,
                RequestTimeoutSeconds = 10,
                Servers =
                [
                    new McpServerDefinition
                    {
                        Id = "test-server",
                        Name = "Test server",
                        Command = "powershell.exe",
                        Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath]
                    }
                ]
            };

            await using var session = await service.OpenSessionAsync(settings);
            var tool = Assert.Single(session.Tools);
            Assert.Equal("greet", tool.ToolName);
            Assert.Equal("string", tool.Parameters["name"].Type);
            Assert.Contains(session.StartupTrace, entry => !entry.IsError && entry.Output.Contains("1 tool", StringComparison.Ordinal));

            var result = await session.ExecuteAsync(tool.ModelToolName, """{"name":"Ada"}""");

            Assert.False(result.IsError);
            Assert.Equal("mcp.test_server.greet", result.Tool);
            Assert.Contains("Hello, Ada", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // The test server is stopped by the session; a transient Windows file lock is harmless.
            }
        }
    }

    [Fact]
    public async Task OpenSessionAsync_KeepsAFailedServerVisibleInTraceWithoutThrowing()
    {
        var service = new McpToolService();
        var settings = new McpSettings
        {
            Enabled = true,
            Servers = [new McpServerDefinition { Name = "Missing", Command = "" }]
        };

        await using var session = await service.OpenSessionAsync(settings);

        Assert.Empty(session.Tools);
        Assert.Contains(session.StartupTrace, entry => entry.IsError && entry.Output.Contains("No command", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenSessionAsync_StopsServerThatWritesAnOversizedProtocolFrame()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"lucky-mcp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "oversized-mcp.ps1");
        await File.WriteAllTextAsync(scriptPath, OversizedFrameServerScript);

        try
        {
            var service = new McpToolService();
            var settings = new McpSettings
            {
                Enabled = true,
                RequestTimeoutSeconds = 10,
                Servers =
                [
                    new McpServerDefinition
                    {
                        Id = "oversized",
                        Name = "Oversized server",
                        Command = "powershell.exe",
                        Arguments = ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath]
                    }
                ]
            };

            await using var session = await service.OpenSessionAsync(settings);

            Assert.Empty(session.Tools);
            Assert.Contains(session.StartupTrace, entry =>
                entry.IsError && entry.Output.Contains("protocol frame", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // The rejected server is terminated by the session; a transient lock is harmless.
            }
        }
    }

    private const string TestServerScript = """
        while (($line = [Console]::In.ReadLine()) -ne $null) {
            $request = $line | ConvertFrom-Json
            if ($request.method -eq 'initialize') {
                $response = @{
                    jsonrpc = '2.0'
                    id = $request.id
                    result = @{
                        protocolVersion = '2025-11-25'
                        capabilities = @{ tools = @{} }
                        serverInfo = @{ name = 'test'; version = '1.0' }
                    }
                }
                [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 10 -Compress))
                [Console]::Out.Flush()
                continue
            }

            if ($request.method -eq 'tools/list') {
                $response = @{
                    jsonrpc = '2.0'
                    id = $request.id
                    result = @{
                        tools = @(@{
                            name = 'greet'
                            description = 'Greet a name'
                            inputSchema = @{
                                type = 'object'
                                properties = @{ name = @{ type = 'string'; description = 'Name to greet' } }
                                required = @('name')
                            }
                        })
                    }
                }
                [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 10 -Compress))
                [Console]::Out.Flush()
                continue
            }

            if ($request.method -eq 'tools/call') {
                $response = @{
                    jsonrpc = '2.0'
                    id = $request.id
                    result = @{
                        content = @(@{ type = 'text'; text = "Hello, $($request.params.arguments.name)!" })
                        isError = $false
                    }
                }
                [Console]::Out.WriteLine(($response | ConvertTo-Json -Depth 10 -Compress))
                [Console]::Out.Flush()
            }
        }
        """;

    private const string OversizedFrameServerScript = """
        $null = [Console]::In.ReadLine()
        [Console]::Out.Write(('x' * 262145))
        [Console]::Out.WriteLine()
        [Console]::Out.Flush()
        Start-Sleep -Seconds 30
        """;
}
