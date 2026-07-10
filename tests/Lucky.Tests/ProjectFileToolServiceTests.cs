using System.Text;
using Lucky.Core;

namespace Lucky.Tests;

public sealed class ProjectFileToolServiceTests
{
    [Fact]
    public async Task ReadAsync_RejectsPathOutsideProject()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectFileToolService();

            var result = await service.ReadAsync(project, @"..\outside.txt");

            Assert.True(result.IsError);
            Assert.Contains("outside", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ReadAsync_ReadsUtf8TextAndRefusesBinary()
    {
        var (root, project) = CreateProject();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# Lucky\nagent harness");
            await File.WriteAllBytesAsync(Path.Combine(root, "image.bin"), [1, 2, 0, 4]);
            var service = new ProjectFileToolService();

            var text = await service.ReadAsync(project, "README.md");
            var binary = await service.ReadAsync(project, "image.bin");

            Assert.False(text.IsError);
            Assert.Contains("agent harness", text.Output);
            Assert.True(binary.IsError);
            Assert.Contains("binary", binary.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SearchAsync_RespectsExcludedDirectories()
    {
        var (root, project) = CreateProject();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "src.txt"), "find this line");
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            await File.WriteAllTextAsync(Path.Combine(root, "bin", "generated.txt"), "find this generated line");
            var service = new ProjectFileToolService();

            var result = await service.SearchAsync(project, "find this", glob: "*.txt");

            Assert.False(result.IsError);
            Assert.Contains("src.txt", result.Output);
            Assert.DoesNotContain("generated.txt", result.Output);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SearchAsync_DoesNotFollowDirectoryReparsePointsOutsideProject()
    {
        var (root, project) = CreateProject();
        var outsideRoot = Path.Combine(Path.GetTempPath(), "Lucky.ProjectFileToolServiceOutside", Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(root, "linked-outside");
        try
        {
            Directory.CreateDirectory(outsideRoot);
            await File.WriteAllTextAsync(Path.Combine(outsideRoot, "secret.txt"), "outside sentinel");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (UnauthorizedAccessException)
            {
                // Some Windows test environments do not permit unprivileged symlink creation.
                return;
            }

            var service = new ProjectFileToolService();
            var result = await service.SearchAsync(project, "outside sentinel", glob: "*.txt");

            Assert.False(result.IsError);
            Assert.DoesNotContain("outside sentinel", result.Output, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            DeleteRoot(root);
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SearchAsync_HonorsCancellationBeforeEnumeratingFiles()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectFileToolService();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SearchAsync(project, "anything", cancellationToken: cancellation.Token));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task SearchAsync_ReportsRegexTimeoutAsAToolError()
    {
        var (root, project) = CreateProject();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "input.txt"), new string('a', 20_000) + "!");
            var service = new ProjectFileToolService();

            var result = await service.SearchAsync(project, "(a+)+$", glob: "*.txt");

            Assert.True(result.IsError);
            Assert.Contains("too long", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task WriteAndEditAsync_CreateAndModifyFilesInsideProject()
    {
        var (root, project) = CreateProject();
        try
        {
            var service = new ProjectFileToolService();

            var write = await service.WriteAsync(project, @"docs\note.md", "hello Lucky", overwrite: false);
            var edit = await service.EditAsync(project, @"docs\note.md", "hello", "hi");
            var content = await File.ReadAllTextAsync(Path.Combine(root, "docs", "note.md"));

            Assert.False(write.IsError);
            Assert.False(edit.IsError);
            Assert.Equal("hi Lucky", content);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task EditAsync_RejectsAmbiguousReplacement()
    {
        var (root, project) = CreateProject();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "note.txt"), "same same");
            var service = new ProjectFileToolService();

            var result = await service.EditAsync(project, "note.txt", "same", "other");

            Assert.True(result.IsError);
            Assert.Contains("matched 2", result.Output);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplyPatchAsync_AppliesUniqueNearbyContextAndPreservesLineEndings()
    {
        var (root, project) = CreateProject();
        try
        {
            var path = Path.Combine(root, "note.txt");
            await File.WriteAllTextAsync(path, "intro\r\ntarget\r\nold value\r\n");
            var service = new ProjectFileToolService();
            var patch = """
--- a/note.txt
+++ b/note.txt
@@ -1,2 +1,2 @@
 target
-old value
+new value
""";

            var result = await service.ApplyPatchAsync(project, patch);

            Assert.False(result.IsError);
            Assert.Equal("intro\r\ntarget\r\nnew value\r\n", await File.ReadAllTextAsync(path));
            Assert.Contains("unique nearby context", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("project.apply_patch", result.Tool);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplyPatchAsync_ValidatesAllFilesBeforeChangingAnyFile()
    {
        var (root, project) = CreateProject();
        try
        {
            var first = Path.Combine(root, "first.txt");
            var second = Path.Combine(root, "second.txt");
            await File.WriteAllTextAsync(first, "alpha\n");
            await File.WriteAllTextAsync(second, "bravo\n");
            var service = new ProjectFileToolService();
            var patch = """
--- a/first.txt
+++ b/first.txt
@@ -1 +1 @@
-alpha
+changed
--- a/second.txt
+++ b/second.txt
@@ -1 +1 @@
-missing
+changed
""";

            var result = await service.ApplyPatchAsync(project, patch);

            Assert.True(result.IsError);
            Assert.Contains("could not find", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("alpha\n", await File.ReadAllTextAsync(first));
            Assert.Equal("bravo\n", await File.ReadAllTextAsync(second));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplyPatchAsync_CreatesAndDeletesFilesFromOnePatch()
    {
        var (root, project) = CreateProject();
        try
        {
            var removedPath = Path.Combine(root, "remove.txt");
            await File.WriteAllTextAsync(removedPath, "remove me\n");
            var service = new ProjectFileToolService();
            var patch = """
--- /dev/null
+++ b/new.txt
@@ -0,0 +1,2 @@
+new line
+second line
--- a/remove.txt
+++ /dev/null
@@ -1 +0,0 @@
-remove me
""";

            var result = await service.ApplyPatchAsync(project, patch);

            Assert.False(result.IsError);
            Assert.Equal($"new line{Environment.NewLine}second line{Environment.NewLine}", await File.ReadAllTextAsync(Path.Combine(root, "new.txt")));
            Assert.False(File.Exists(removedPath));
            Assert.Contains("created 1", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("deleted 1", result.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [Fact]
    public async Task ApplyPatchAsync_RejectsPatchPathOutsideProject()
    {
        var (root, project) = CreateProject();
        var outside = Path.Combine(Path.GetTempPath(), $"Lucky.PatchOutside.{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(outside, "outside\n");
            var service = new ProjectFileToolService();
            var patch = """
--- a/../outside.txt
+++ b/../outside.txt
@@ -1 +1 @@
-outside
+changed
""";

            var result = await service.ApplyPatchAsync(project, patch);

            Assert.True(result.IsError);
            Assert.Contains("outside", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("outside\n", await File.ReadAllTextAsync(outside));
        }
        finally
        {
            DeleteRoot(root);
            if (File.Exists(outside))
            {
                File.Delete(outside);
            }
        }
    }

    [Fact]
    public async Task ApplyPatchAsync_DoesNotFollowDirectoryReparsePointsOutsideProject()
    {
        var (root, project) = CreateProject();
        var outsideRoot = Path.Combine(Path.GetTempPath(), "Lucky.PatchOutsideLink", Guid.NewGuid().ToString("N"));
        var linkPath = Path.Combine(root, "linked-outside");
        var outsideFile = Path.Combine(outsideRoot, "secret.txt");
        try
        {
            Directory.CreateDirectory(outsideRoot);
            await File.WriteAllTextAsync(outsideFile, "outside\n");
            try
            {
                Directory.CreateSymbolicLink(linkPath, outsideRoot);
            }
            catch (UnauthorizedAccessException)
            {
                // Some Windows test environments do not permit unprivileged symlink creation.
                return;
            }

            var service = new ProjectFileToolService();
            var patch = """
                --- a/linked-outside/secret.txt
                +++ b/linked-outside/secret.txt
                @@ -1 +1 @@
                -outside
                +changed
                """;

            var result = await service.ApplyPatchAsync(project, patch);

            Assert.True(result.IsError);
            Assert.Contains("reparse", result.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("outside\n", await File.ReadAllTextAsync(outsideFile));
        }
        finally
        {
            if (Directory.Exists(linkPath))
            {
                Directory.Delete(linkPath);
            }

            DeleteRoot(root);
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    private static (string Root, LuckyProject Project) CreateProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "Lucky.ProjectFileToolServiceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (root, new LuckyProject { Name = "Test", Path = root });
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
