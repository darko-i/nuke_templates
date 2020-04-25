using System;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.XmlTasks;
using static Nuke.Common.IO.TextTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using System.Collections.Generic;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main() => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Version number to use - Default is autoincremented previous version number")]
    readonly string Version;

    [Parameter("Destination NuGet server URL - Default is http://jenkins:8081/nuget")]
    readonly string NuGetServer = "http://jenkins:8081/nuget";

    [Parameter("Destination NuGet symbol server URL - Default is http://jenkins:8082/nuget")]
    readonly string NuGetSymbolServer = "http://jenkins:8082/nuget";

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath OutputDirectory => RootDirectory / "output";

    private string BuildVersion;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    Target Restore => _ => _
        .Before(Compile)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Publish => _ => _
        .DependsOn(Clean, Restore, UpdateVersion, Compile)
        .Executes(() =>
        {

            var nupkgs = GlobFiles(RootDirectory, $"src\\**\\bin\\{Configuration}\\*.nupkg");
            
            var packages = new List<string>();
            var symbolPackages = new List<string>();

            foreach (var file in nupkgs)
            {
                if (file.Contains(".symbols."))
                    symbolPackages.Add(file);
                else
                    packages.Add(file);
            }

            foreach (var p in packages)
            {
                var settings = new DotNetNuGetPushSettings()
                    .SetSource(NuGetServer)
                    .SetTargetPath(p);

                DotNetNuGetPush(settings);
            }
        });

    Target PrepareGitTag => _ => _
        .DependsOn(UpdateVersion)
        .Executes(() =>
        {
            WriteAllLines(RootDirectory / "TagVersion", new string[] { $"TagVersion={BuildVersion}" });
        });

    Target UpdateVersion => _ => _
        .Executes(() =>
        {
            if (!string.IsNullOrEmpty(Version))
            { 
                var isVersionValid = CheckVersionString(Version);
                if (isVersionValid)
                    BuildVersion = Version;
                else
                    throw new Exception($"Invalid Version parameter specified: {Version}");
            }
            else
            {
                var buildVersionFromXml = GetVersion();
                var tmpArray = buildVersionFromXml.Split('.');
                int incBuild = int.Parse(tmpArray[2]); // must convert to int
                incBuild++;
                BuildVersion = $"{tmpArray[0]}.{tmpArray[1]}.{incBuild}.0";
            }

            foreach (var proj in Solution.AllProjects)
            {
                var publishPackage = proj.GetProperty<bool?>("__PublishPackage").GetValueOrDefault();
                if (publishPackage)
                {
                    SetProjectVersion(proj.Path, BuildVersion);
                }
            }

            XmlPoke(RootDirectory / "Version.xml", "/Version", BuildVersion);
        });

    private static bool CheckVersionString(string version)
    {
        return Regex.IsMatch(version, "^(\\d+\\.){3}(\\d+)$");
    }

    private void SetProjectVersion(string projectFile, string version)
    {
        XmlPoke(projectFile, "/Project/PropertyGroup/AssemblyVersion", version);
        XmlPoke(projectFile, "/Project/PropertyGroup/FileVersion", version);
        XmlPoke(projectFile, "/Project/PropertyGroup/Version", version);
    }
    private static string GetVersion()
    {
        var path = RootDirectory / "version.xml";
        return XmlPeekSingle(path, "/Version");
    }
}
