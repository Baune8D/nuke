﻿// Copyright 2021 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using Nuke.GlobalTool.Rewriting.Cake;
using static Nuke.Common.Constants;
using static Nuke.Common.EnvironmentInfo;

namespace Nuke.GlobalTool
{
    partial class Program
    {
        public const string CAKE_FILE_PATTERN = "*.cake";

        [UsedImplicitly]
        public static int CakeConvert(string[] args, [CanBeNull] AbsolutePath rootDirectory, [CanBeNull] AbsolutePath buildScript)
        {
            PrintInfo();
            Logging.Configure();
            Telemetry.ConvertCake();
            ProjectModelTasks.Initialize();

            Host.Warning(
                new[]
                {
                    "Converting .cake files is a best effort approach using syntax rewriting.",
                    "Compile errors are to be expected, however, the following elements are currently covered:",
                    "  - Target definitions",
                    "  - Default targets",
                    "  - Parameter declarations",
                    "  - Absolute paths",
                    "  - Globbing patterns",
                    "  - Tool invocations (dotnet CLI, SignTool)",
                    "  - Addin and tool references",
                }.JoinNewLine());

            Host.Debug();
            if (!PromptForConfirmation("Continue?"))
                return 0;
            Host.Debug();

            if (buildScript == null &&
                PromptForConfirmation("Should a NUKE project be created for better results?"))
            {
                Setup(args, rootDirectory: null, buildScript: null);
            }

            var buildScriptFile = WorkingDirectory / CurrentBuildScriptName;
            var buildProjectFile = buildScriptFile.Exists()
                ? GetConfiguration(buildScriptFile, evaluate: true)
                    .GetValueOrDefault(BUILD_PROJECT_FILE, defaultValue: null)
                : null;

            AbsolutePath GetOutputDirectory(AbsolutePath file)
                => Path.GetDirectoryName(buildProjectFile ?? file);

            AbsolutePath GetOutputFile(AbsolutePath file)
                => GetOutputDirectory(file) / Path.GetFileNameWithoutExtension(file).Capitalize() + ".cs";

            GetCakeFiles().ForEach(x => File.WriteAllText(path: GetOutputFile(x), contents: GetCakeConvertedContent(File.ReadAllText(x))));

            if (buildProjectFile != null)
            {
                GetCakeFiles().SelectMany(x => GetCakePackages(File.ReadAllText(x)))
                    .ForEach(x => AddOrReplacePackage(x.PackageId, x.PackageVersion, x.PackageType, buildProjectFile));
            }

            return 0;
        }

        [UsedImplicitly]
        public static int CakeClean(string[] args, [CanBeNull] AbsolutePath rootDirectory, [CanBeNull] AbsolutePath buildScript)
        {
            var cakeFiles = GetCakeFiles().ToList();
            Host.Information("Found .cake files:");
            cakeFiles.ForEach(x => Host.Debug($"  - {x}"));

            if (PromptForConfirmation("Delete?"))
                cakeFiles.ForEach(x => x.DeleteFile());

            return 0;
        }

        private static IEnumerable<AbsolutePath> GetCakeFiles()
        {
            return (TryGetRootDirectoryFrom(WorkingDirectory) ?? WorkingDirectory).GlobFiles($"**/{CAKE_FILE_PATTERN}");
        }

        internal static string GetCakeConvertedContent(string content)
        {
            var options = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.None, SourceCodeKind.Script);
            var syntaxTree = CSharpSyntaxTree.ParseText(content, options);
            return new CSharpSyntaxRewriter[]
                   {
                       new RemoveUsingDirectivesRewriter(),
                       new RenameFieldIdentifierRewriter(),
                       new ParameterRewriter(),
                       new AbsolutePathRewriter(),
                       new RegularFieldRewriter(),
                       new TargetDefinitionRewriter(),
                       new InvocationRewriter(),
                       new MemberAccessRewriter(),
                       new IdentifierNameRewriter(),
                       new ToolInvocationRewriter(),
                       new ClassRewriter(),
                       new FormattingRewriter()
                   }.Aggregate(syntaxTree.GetRoot(), (root, rewriter) => rewriter.Visit(root.NormalizeWhitespace(elasticTrivia: true)))
                .ToFullString();
        }

        internal static IEnumerable<(string PackageType, string PackageId, string PackageVersion)> GetCakePackages(string content)
        {
            IEnumerable<(string PackageType, string PackageId, string PackageVersion)> GetPackages(
                string packageType,
                [RegexPattern] string regexPattern)
            {
                var regex = new Regex(regexPattern);
                foreach (Match match in regex.Matches(content))
                {
                    var packageId = match.Groups["packageId"].Value;
                    var packageVersion = match.Groups["version"].Value;
                    if (packageVersion.IsNullOrEmpty())
                        packageVersion = NuGetVersionResolver.GetLatestVersion(packageId, includePrereleases: false).GetAwaiter().GetResult();
                    yield return new(packageType, packageId, packageVersion);
                }
            }

            return GetPackages(PACKAGE_TYPE_DOWNLOAD, @"#tool ""nuget:\?package=(?'packageId'[\w\d\.]+)(&version=(?'version'[\w\d\.]+))?S*""")
                .Concat(GetPackages(PACKAGE_TYPE_REFERENCE, @"#addin ""nuget:\?package=(?'packageId'[\w\d\.]+)(&version=(?'version'[\w\d\.]+))?S*"""))
                .Where(x => !x.PackageId.ContainsOrdinalIgnoreCase("Cake"));
        }
    }
}
