using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Assembly = UnityEditor.Compilation.Assembly;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace OpenIDEA.Unity
{
    /// Writes the solution and one .csproj per assembly definition, in the same
    /// (non-SDK) shape as Rider's package, which csharp-ls loads without a restore.
    internal static class ProjectGeneration
    {
        static readonly string[] BuiltinExtensions =
        {
            "cs", "uxml", "uss", "shader", "compute", "cginc", "hlsl", "glslinc", "template", "raytrace",
            "json", "rsp", "asmdef", "asmref", "xaml", "tt", "t4", "ttinclude", "txt", "xml", "md", "yaml", "yml",
        };

        static readonly string[] SyncTriggers = { ".cs", ".asmdef", ".asmref", ".dll", ".rsp" };

        static string ProjectDirectory => OpenIdeaCodeEditor.ProjectRoot.Replace('\\', '/');

        public static bool IsSupportedFile(string path)
        {
            var extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (extension.Length == 0) return false;
            return BuiltinExtensions.Contains(extension) || EditorSettings.projectGenerationUserExtensions.Contains(extension);
        }

        public static void SyncIfNeeded(IEnumerable<string> changedFiles)
        {
            if (changedFiles.Any(f => SyncTriggers.Contains(Path.GetExtension(f).ToLowerInvariant())))
                Sync(false);
        }

        /// Regenerates every file; only files whose content changed are written
        /// (`force` writes them all), so the language server reloads only what moved.
        public static void Sync(bool force)
        {
            try
            {
                var assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                    .Where(a => a.sourceFiles.Length > 0 && ShouldGenerate(a))
                    .OrderBy(a => a.name, StringComparer.Ordinal)
                    .ToArray();
                var generated = new HashSet<string>(assemblies.Select(a => a.name));
                var others = OtherFilesByAssembly(assemblies);

                foreach (var assembly in assemblies)
                {
                    others.TryGetValue(assembly.name, out var extra);
                    Write(Path.Combine(ProjectDirectory, assembly.name + ".csproj"), ProjectText(assembly, generated, extra), force);
                }
                Write(Path.Combine(ProjectDirectory, Path.GetFileName(ProjectDirectory) + ".sln"), SolutionText(assemblies), force);
            }
            catch (Exception e)
            {
                Debug.LogError("[OpenIDEA] Project generation failed: " + e);
            }
        }

        static bool ShouldGenerate(Assembly assembly)
        {
            var package = PackageInfo.FindForAssetPath(assembly.sourceFiles[0]);
            return package == null || OpenIdeaSettings.GeneratesFor(package.source);
        }

        /// Non-script assets (shaders, UXML, …) listed under the assembly whose folder holds them, as Rider does.
        static Dictionary<string, List<string>> OtherFilesByAssembly(Assembly[] assemblies)
        {
            var result = new Dictionary<string, List<string>>();
            var firstPass = assemblies.FirstOrDefault(a => a.name == "Assembly-CSharp")?.name ?? assemblies.FirstOrDefault()?.name;
            var roots = assemblies
                .Select(a => (a.name, dir: CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(a.name)))
                .Where(x => !string.IsNullOrEmpty(x.dir))
                .Select(x => (x.name, dir: Path.GetDirectoryName(x.dir).Replace('\\', '/') + "/"))
                .OrderByDescending(x => x.dir.Length)
                .ToArray();

            foreach (var asset in AssetDatabase.GetAllAssetPaths())
            {
                if (!asset.StartsWith("Assets/", StringComparison.Ordinal) && !asset.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                var extension = Path.GetExtension(asset).ToLowerInvariant();
                if (extension == ".cs" || !IsSupportedFile(asset)) continue;
                var owner = roots.FirstOrDefault(r => asset.StartsWith(r.dir, StringComparison.Ordinal)).name;
                if (owner == null)
                {
                    if (!asset.StartsWith("Assets/", StringComparison.Ordinal) || firstPass == null) continue;
                    owner = firstPass;
                }
                if (!result.TryGetValue(owner, out var list)) result[owner] = list = new List<string>();
                list.Add(asset);
            }
            return result;
        }

        static string ProjectText(Assembly assembly, HashSet<string> generated, List<string> otherFiles)
        {
            var options = assembly.compilerOptions;
            var defines = assembly.defines.Concat(assembly.compilerOptions.ResponseFiles != null ? ResponseFileDefines(assembly) : Enumerable.Empty<string>())
                .Distinct().ToArray();
            var langVersion = string.IsNullOrEmpty(options.LanguageVersion) ? "9.0" : options.LanguageVersion;

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<!-- Generated by com.openidea.ide.unity. Unity rewrites this file: do not edit it. -->");
            sb.AppendLine("<Project ToolsVersion=\"4.0\" DefaultTargets=\"Build\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine($"    <LangVersion>{Escape(langVersion)}</LangVersion>");
            sb.AppendLine("    <_TargetFrameworkDirectories>non_empty_path_generated_by_openidea</_TargetFrameworkDirectories>");
            sb.AppendLine("    <_FullFrameworkReferenceAssemblyPaths>non_empty_path_generated_by_openidea</_FullFrameworkReferenceAssemblyPaths>");
            sb.AppendLine("    <DisableHandlePackageFileConflicts>true</DisableHandlePackageFileConflicts>");
            sb.AppendLine("    <ResolveNuGetPackages>false</ResolveNuGetPackages>");
            sb.AppendLine("  </PropertyGroup>");
            sb.AppendLine("  <PropertyGroup>");
            sb.AppendLine("    <Configuration Condition=\" '$(Configuration)' == '' \">Debug</Configuration>");
            sb.AppendLine("    <Platform Condition=\" '$(Platform)' == '' \">AnyCPU</Platform>");
            sb.AppendLine($"    <ProjectGuid>{{{Guid(assembly.name)}}}</ProjectGuid>");
            sb.AppendLine("    <ProjectTypeGuids>{E097FAD1-6243-4DAD-9C02-E9B9EFC3FFC1};{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}</ProjectTypeGuids>");
            sb.AppendLine($"    <RootNamespace>{Escape(assembly.rootNamespace ?? "")}</RootNamespace>");
            sb.AppendLine("    <OutputType>Library</OutputType>");
            sb.AppendLine($"    <AssemblyName>{Escape(assembly.name)}</AssemblyName>");
            sb.AppendLine("    <TargetFrameworkVersion>v4.7.1</TargetFrameworkVersion>");
            sb.AppendLine("    <FileAlignment>512</FileAlignment>");
            sb.AppendLine("    <BaseDirectory>.</BaseDirectory>");
            sb.AppendLine("    <DebugSymbols>true</DebugSymbols>");
            sb.AppendLine("    <DebugType>full</DebugType>");
            sb.AppendLine("    <Optimize>false</Optimize>");
            sb.AppendLine($"    <OutputPath>Temp/bin/Debug/{Escape(assembly.name)}/</OutputPath>");
            sb.AppendLine($"    <DefineConstants>{Escape(string.Join(";", defines))}</DefineConstants>");
            sb.AppendLine("    <ErrorReport>prompt</ErrorReport>");
            sb.AppendLine("    <WarningLevel>4</WarningLevel>");
            sb.AppendLine("    <NoWarn>0169,0649</NoWarn>");
            sb.AppendLine($"    <AllowUnsafeBlocks>{options.AllowUnsafeCode}</AllowUnsafeBlocks>");
            sb.AppendLine("    <TreatWarningsAsErrors>False</TreatWarningsAsErrors>");
            sb.AppendLine("    <NoConfig>true</NoConfig>");
            sb.AppendLine("    <NoStdLib>true</NoStdLib>");
            sb.AppendLine("    <AddAdditionalExplicitAssemblyReferences>false</AddAdditionalExplicitAssemblyReferences>");
            sb.AppendLine("    <ImplicitlyExpandNETStandardFacades>false</ImplicitlyExpandNETStandardFacades>");
            sb.AppendLine("    <ImplicitlyExpandDesignTimeFacades>false</ImplicitlyExpandDesignTimeFacades>");
            sb.AppendLine("  </PropertyGroup>");

            var analyzers = options.RoslynAnalyzerDllPaths ?? Array.Empty<string>();
            if (analyzers.Length > 0)
            {
                sb.AppendLine("  <ItemGroup>");
                foreach (var analyzer in analyzers.Distinct())
                    sb.AppendLine($"    <Analyzer Include=\"{Escape(FullPath(analyzer))}\" />");
                sb.AppendLine("  </ItemGroup>");
            }

            sb.AppendLine("  <ItemGroup>");
            foreach (var file in assembly.sourceFiles)
                sb.AppendLine($"    <Compile Include=\"{Escape(Relative(file))}\" />");
            if (otherFiles != null)
                foreach (var file in otherFiles)
                    sb.AppendLine($"    <None Include=\"{Escape(Relative(file))}\" />");
            sb.AppendLine("  </ItemGroup>");

            sb.AppendLine("  <ItemGroup>");
            var references = assembly.compiledAssemblyReferences
                .Concat(assembly.assemblyReferences.Where(r => !generated.Contains(r.name)).Select(r => r.outputPath))
                .Distinct();
            foreach (var reference in references)
            {
                var path = FullPath(reference);
                sb.AppendLine($"    <Reference Include=\"{Escape(Path.GetFileNameWithoutExtension(path))}\">");
                sb.AppendLine($"      <HintPath>{Escape(path)}</HintPath>");
                sb.AppendLine("    </Reference>");
            }
            foreach (var reference in assembly.assemblyReferences.Where(r => generated.Contains(r.name)))
            {
                sb.AppendLine($"    <ProjectReference Include=\"{Escape(reference.name)}.csproj\">");
                sb.AppendLine($"      <Project>{{{Guid(reference.name)}}}</Project>");
                sb.AppendLine($"      <Name>{Escape(reference.name)}</Name>");
                sb.AppendLine("    </ProjectReference>");
            }
            sb.AppendLine("  </ItemGroup>");
            sb.AppendLine("  <Import Project=\"$(MSBuildToolsPath)\\Microsoft.CSharp.targets\" />");
            sb.AppendLine("</Project>");
            return sb.ToString();
        }

        /// `-define:` / `-d:` lines of the assembly's response files (csc.rsp).
        static IEnumerable<string> ResponseFileDefines(Assembly assembly)
        {
            foreach (var rsp in assembly.compilerOptions.ResponseFiles)
            {
                var path = FullPath(rsp);
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path))
                {
                    var trimmed = line.Trim();
                    foreach (var prefix in new[] { "-define:", "/define:", "-d:", "/d:" })
                    {
                        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        foreach (var define in trimmed.Substring(prefix.Length).Split(';', ','))
                            if (define.Trim().Length > 0) yield return define.Trim();
                    }
                }
            }
        }

        static string SolutionText(Assembly[] assemblies)
        {
            const string csharp = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";
            var sb = new StringBuilder();
            sb.Append("\r\nMicrosoft Visual Studio Solution File, Format Version 11.00\r\n# Visual Studio 2010\r\n");
            foreach (var a in assemblies)
                sb.Append($"Project(\"{csharp}\") = \"{a.name}\", \"{a.name}.csproj\", \"{{{Guid(a.name)}}}\"\r\nEndProject\r\n");
            sb.Append("Global\r\n");
            sb.Append("\tGlobalSection(SolutionConfigurationPlatforms) = preSolution\r\n\t\tDebug|Any CPU = Debug|Any CPU\r\n\tEndGlobalSection\r\n");
            sb.Append("\tGlobalSection(ProjectConfigurationPlatforms) = postSolution\r\n");
            foreach (var a in assemblies)
            {
                sb.Append($"\t\t{{{Guid(a.name)}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU\r\n");
                sb.Append($"\t\t{{{Guid(a.name)}}}.Debug|Any CPU.Build.0 = Debug|Any CPU\r\n");
            }
            sb.Append("\tEndGlobalSection\r\n");
            sb.Append("\tGlobalSection(SolutionProperties) = preSolution\r\n\t\tHideSolutionNode = FALSE\r\n\tEndGlobalSection\r\n");
            sb.Append("EndGlobal\r\n");
            return sb.ToString();
        }

        static void Write(string path, string text, bool force)
        {
            if (!force && File.Exists(path) && File.ReadAllText(path) == text) return;
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        /// Stable per name, so projects keep their GUID across regenerations.
        static string Guid(string name)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes("openidea:" + name));
                return new System.Guid(bytes).ToString();
            }
        }

        // Unity's Path.GetFullPath maps virtual "Packages/<name>/…" paths to where the package really is
        // (Library/PackageCache, a local folder…); the editor's working directory is the project root.
        static string FullPath(string path) =>
            (Path.IsPathRooted(path) ? path : Path.GetFullPath(path)).Replace('\\', '/');

        static string Relative(string path)
        {
            var full = FullPath(path);
            var root = ProjectDirectory.TrimEnd('/') + "/";
            return full.StartsWith(root, StringComparison.Ordinal) ? full.Substring(root.Length) : full;
        }

        static string Escape(string s) => System.Security.SecurityElement.Escape(s);
    }
}
