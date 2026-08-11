using System.Xml.Linq;

namespace RcloneUI.Architecture.Tests;

public sealed class ProjectDependencyTests
{
    private static readonly Dictionary<string, string[]> AllowedReferences =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["RcloneUI.Contracts"] = [],
            ["RcloneUI.DataRoot"] = ["RcloneUI.Contracts"],
            ["RcloneUI.Rclone"] = ["RcloneUI.Contracts"],
            ["RcloneUI.Remotes"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot", "RcloneUI.Rclone"],
            ["RcloneUI.Transfers"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot", "RcloneUI.Rclone"],
            ["RcloneUI.Mounts"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot", "RcloneUI.Rclone"],
            ["RcloneUI.Work"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot", "RcloneUI.Mounts", "RcloneUI.Remotes", "RcloneUI.Transfers"],
            ["RcloneUI.Updates"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot", "RcloneUI.Rclone"],
            ["RcloneUI.Diagnostics"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot"],
            ["RcloneUI.Desktop"] = ["RcloneUI.Contracts"],
            ["RcloneUI.Updater"] = ["RcloneUI.Contracts"],
            ["RcloneUI.Host"] = ["RcloneUI.Contracts", "RcloneUI.DataRoot", "RcloneUI.Diagnostics", "RcloneUI.Mounts", "RcloneUI.Rclone", "RcloneUI.Remotes", "RcloneUI.Transfers", "RcloneUI.Updates", "RcloneUI.Work"]
        };

    [Fact]
    public void ProductionProjectReferencesFollowTheModuleMap()
    {
        var root = FindRepositoryRoot();
        var projects = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
        Assert.Equal(AllowedReferences.Count, projects.Length);

        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            Assert.True(AllowedReferences.TryGetValue(name, out var allowed), $"Unexpected production project: {name}");
            var actual = XDocument.Load(project)
                .Descendants("ProjectReference")
                .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value))
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(allowed!.Order(StringComparer.Ordinal), actual);
        }
    }

    [Fact]
    public void OnlyTheDesktopReferencesAvaloniaPackages()
    {
        var root = FindRepositoryRoot();
        foreach (var project in Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var packages = XDocument.Load(project).Descendants("PackageReference")
                .Select(item => item.Attribute("Include")!.Value)
                .Where(name => name.StartsWith("Avalonia", StringComparison.Ordinal))
                .ToArray();
            if (Path.GetFileNameWithoutExtension(project) == "RcloneUI.Desktop")
            {
                Assert.NotEmpty(packages);
            }
            else
            {
                Assert.Empty(packages);
            }
        }
    }

    [Fact]
    public void ContractsHasNoImplementationDependencies()
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "src", "RcloneUI.Contracts", "RcloneUI.Contracts.csproj"));

        Assert.Empty(document.Descendants("PackageReference"));
        Assert.Empty(document.Descendants("ProjectReference"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RcloneUI.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate RcloneUI.slnx.");
    }
}
