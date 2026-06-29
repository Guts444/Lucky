using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lucky.Core;

public sealed class SubagentDefinitionService
{
    private static readonly JsonSerializerOptions FileJsonOptions = CreateFileJsonOptions();

    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal)
    {
        "project_list_files",
        "project_read_file",
        "project_search",
        "project_write_file",
        "project_edit_file"
    };

    private static JsonSerializerOptions CreateFileJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public async Task<IReadOnlyList<SubagentDefinition>> LoadAsync(
        LuckyState state,
        LuckyProject? project,
        CancellationToken cancellationToken = default)
    {
        var definitions = new Dictionary<string, SubagentDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in BuiltIns())
        {
            AddOrReplace(definitions, definition);
        }

        foreach (var definition in state.Settings.Subagents.CustomAgents)
        {
            AddOrReplace(definitions, definition);
        }

        foreach (var definition in await LoadFileDefinitionsAsync(ProfileAgentsDirectory(), cancellationToken).ConfigureAwait(false))
        {
            AddOrReplace(definitions, definition);
        }

        if (project is not null)
        {
            foreach (var definition in await LoadProjectDefinitionsAsync(project, cancellationToken).ConfigureAwait(false))
            {
                AddOrReplace(definitions, definition);
            }
        }

        return definitions.Values
            .Where(definition => definition.Enabled)
            .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SubagentDefinition> BuiltIns() =>
    [
        new()
        {
            Name = "explorer",
            Description = "Read-heavy codebase exploration for mapping files, APIs, dependencies, and existing patterns.",
            Instructions = "Explore like a careful senior engineer. Read and search narrowly, return concrete file references, and avoid proposing edits unless asked.",
            Tools = ["project_list_files", "project_read_file", "project_search"],
            AutoActivate = true
        },
        new()
        {
            Name = "reviewer",
            Description = "Review code or plans for bugs, regressions, security risks, edge cases, and missing tests.",
            Instructions = "Review from a correctness-first stance. Lead with actionable findings, include file references when available, and keep summaries compact.",
            Tools = ["project_list_files", "project_read_file", "project_search"],
            AutoActivate = true
        },
        new()
        {
            Name = "tester",
            Description = "Find relevant tests, plan verification, inspect test failures, and summarize likely fixes.",
            Instructions = "Focus on verification. Identify the smallest meaningful test commands or manual checks and explain failures without dumping logs.",
            Tools = ["project_list_files", "project_read_file", "project_search"],
            AutoActivate = true
        },
        new()
        {
            Name = "writer",
            Description = "Documentation-focused agent for README, architecture, QA, and user-visible behavior notes.",
            Instructions = "Focus on clear docs. Check existing documentation ownership rules and return concise update recommendations.",
            Tools = ["project_list_files", "project_read_file", "project_search"],
            AutoActivate = true
        },
        new()
        {
            Name = "worker",
            Description = "Implementation-focused agent for a bounded piece of production work. In Lucky's first subagent version, it returns a patch plan for the parent to apply.",
            Instructions = "Work on one bounded implementation slice. Inspect before advising, respect local conventions, and return exact files and changes needed.",
            Tools = ["project_list_files", "project_read_file", "project_search"],
            AutoActivate = false
        }
    ];

    public static string RenderCatalogForPrompt(IEnumerable<SubagentDefinition> definitions, bool autoDelegateEnabled)
    {
        var visible = definitions.Where(definition => definition.Enabled).ToArray();
        if (visible.Length == 0)
        {
            return "";
        }

        var mode = autoDelegateEnabled
            ? "You may delegate when it would materially improve speed or quality, and the work can be done in parallel."
            : "Only delegate when the user explicitly asks for subagents or a named agent.";
        var lines = new List<string>
        {
            "Available subagents:",
            mode
        };
        foreach (var definition in visible)
        {
            var auto = definition.AutoActivate ? "auto" : "explicit";
            lines.Add($"- {definition.Name} ({auto}): {definition.Description}");
        }

        lines.Add("Use the subagent_run tool with a specific agent name and a concise, self-contained task. Do not delegate trivial single-step work.");
        return string.Join(Environment.NewLine, lines);
    }

    private static void AddOrReplace(IDictionary<string, SubagentDefinition> definitions, SubagentDefinition definition)
    {
        var normalized = Normalize(definition);
        if (normalized is null)
        {
            return;
        }

        definitions[normalized.Name] = normalized;
    }

    private static SubagentDefinition? Normalize(SubagentDefinition definition)
    {
        var name = NormalizeName(definition.Name);
        if (string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(definition.Description) ||
            string.IsNullOrWhiteSpace(definition.Instructions))
        {
            return null;
        }

        var tools = definition.Tools
            .Where(tool => KnownTools.Contains(tool))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (tools.Count == 0)
        {
            tools = ["project_list_files", "project_read_file", "project_search"];
        }

        return new SubagentDefinition
        {
            Name = name,
            Description = definition.Description.Trim(),
            Instructions = definition.Instructions.Trim(),
            Tools = tools,
            Enabled = definition.Enabled,
            AutoActivate = definition.AutoActivate,
            AccessLevel = definition.AccessLevel,
            ModelOverride = string.IsNullOrWhiteSpace(definition.ModelOverride) ? null : definition.ModelOverride.Trim(),
            ReasoningEffortOverride = string.IsNullOrWhiteSpace(definition.ReasoningEffortOverride) ? null : definition.ReasoningEffortOverride.Trim()
        };
    }

    private static string NormalizeName(string name)
    {
        var normalized = string.Join("-", name.Trim().ToLowerInvariant().Split([' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 64 ? normalized : normalized[..64];
    }

    private static string ProfileAgentsDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "Lucky", "agents");
    }

    private static Task<IReadOnlyList<SubagentDefinition>> LoadProjectDefinitionsAsync(
        LuckyProject project,
        CancellationToken cancellationToken)
    {
        try
        {
            var root = Path.GetFullPath(project.Path);
            if (!Directory.Exists(root))
            {
                return Task.FromResult<IReadOnlyList<SubagentDefinition>>([]);
            }

            var directory = Path.GetFullPath(Path.Combine(root, ".lucky", "agents"));
            if (!IsWithinRoot(root, directory))
            {
                return Task.FromResult<IReadOnlyList<SubagentDefinition>>([]);
            }

            return LoadFileDefinitionsAsync(directory, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult<IReadOnlyList<SubagentDefinition>>([]);
        }
    }

    private static async Task<IReadOnlyList<SubagentDefinition>> LoadFileDefinitionsAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var definitions = new List<SubagentDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var definition = await JsonSerializer.DeserializeAsync<SubagentDefinition>(
                    stream,
                    FileJsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (definition is not null)
                {
                    definitions.Add(definition);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
            {
                continue;
            }
        }

        return definitions;
    }

    private static bool IsWithinRoot(string root, string target)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(normalizedRoot, target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison) ||
               target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison) ||
               target.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, comparison);
    }
}
