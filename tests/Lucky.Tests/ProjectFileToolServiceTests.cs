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
