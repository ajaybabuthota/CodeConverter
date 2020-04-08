﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.CodeConverter.CSharp;
using ICSharpCode.CodeConverter.DotNetTool.Util;
using ICSharpCode.CodeConverter.Shared;
using ICSharpCode.CodeConverter.Util;
using ICSharpCode.CodeConverter.VB;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using McMaster.Extensions.CommandLineUtils;
using System.IO;

namespace ICSharpCode.CodeConverter.DotNetTool
{
    public sealed class MSBuildWorkspaceConverter : IDisposable
    {
        private readonly bool _bestEffortConversion;
        private readonly string _solutionFilePath;
        private readonly Dictionary<string, string> _buildProps;
        private Solution _cachedSolution;

        public MSBuildWorkspaceConverter(string solutionFilePath, bool bestEffortConversion = false, Dictionary<string, string> buildProps = null)
        {
            _bestEffortConversion = bestEffortConversion;
            _buildProps ??= new Dictionary<string, string>();
            _buildProps.TryAdd("Configuration", "Debug");
            _buildProps.TryAdd("Platform", "AnyCPU");
            _solutionFilePath = solutionFilePath;
        }

        public async IAsyncEnumerable<ConversionResult> ConvertProjectsWhereAsync(Func<Project, bool> shouldConvertProject, CodeConvProgram.Language? targetLanguage, IProgress<ConversionProgress> progress, [EnumeratorCancellation] CancellationToken token)
        {
            var strProgress = new Progress<string>(s => progress.Report(new ConversionProgress(s)));
            var solution = _cachedSolution ?? (_cachedSolution = await GetSolutionAsync(_solutionFilePath, strProgress));

            if (!targetLanguage.HasValue) {
                targetLanguage = solution.Projects.Any(p => p.Language == LanguageNames.VisualBasic) ? CodeConvProgram.Language.CS : CodeConvProgram.Language.VB;
            }

            var languageConversion = targetLanguage == CodeConvProgram.Language.CS
                ? (ILanguageConversion)new VBToCSConversion()
                : new CSToVBConversion();
            var languageNameToConvert = targetLanguage == CodeConvProgram.Language.CS
                ? LanguageNames.VisualBasic
                : LanguageNames.CSharp;

            var projectsToConvert = solution.Projects.Where(p => p.Language == languageNameToConvert && shouldConvertProject(p)).ToArray();
            var results = SolutionConverter.CreateFor(languageConversion, projectsToConvert, progress, token).Convert();
            await foreach (var r in results) yield return r;
        }

        private async Task<Solution> GetSolutionAsync(string projectOrSolutionFile, IProgress<string> progress)
        {
            progress.Report($"Running dotnet restore on {projectOrSolutionFile}");
            await RestorePackagesForSolutionAsync(projectOrSolutionFile);

            var workspace = await CreateWorkspaceAsync(_buildProps);
            var solution = string.Equals(Path.GetExtension(projectOrSolutionFile), ".sln", StringComparison.OrdinalIgnoreCase) ? await workspace.OpenSolutionAsync(projectOrSolutionFile)
                : (await workspace.OpenProjectAsync(projectOrSolutionFile)).Solution;

            var errorString = await GetCompilationErrorsAsync(solution.Projects, workspace.Diagnostics);
            if (errorString != "") {
                progress.Report($"Please fix compilation erorrs before conversion, or use the best effort conversion option:{Environment.NewLine}{errorString}");
                if (_bestEffortConversion) progress.Report("Attempting best effort conversion on broken input due to override");
                else throw new InvalidOperationException($"Fix compilation erorrs before conversion for an accurate conversion, or use the best effort conversion option:{Environment.NewLine}{errorString}");
            }
            return solution;
        }

        private static async Task<string> GetCompilationErrorsAsync(
            IEnumerable<Project> projectsToConvert, IReadOnlyCollection<WorkspaceDiagnostic> valueDiagnostics)
        {
            var errors = await projectsToConvert.ParallelSelectAwait(async x => {
                var c = await x.GetCompilationAsync();
                return new[] { CompilationWarnings.WarningsForCompilation(c, c.AssemblyName) };
            }, Env.MaxDop, default).ToArrayAsync();
            var solutionErrors = valueDiagnostics.Where(d => d.Kind > WorkspaceDiagnosticKind.Warning).Select(d => d.Message);
            var errorString = string.Join("\r\n", solutionErrors.Concat(errors.SelectMany(w => w).Where(w => w != null)));
            return errorString;
        }

        private static async Task RestorePackagesForSolutionAsync(string solutionFile)
        {
            var dotnetRestore = await ProcessRunner.StartRedirectedToConsoleAsync(DotNetExe.FullPathOrDefault(), "restore", solutionFile);
            if (dotnetRestore.ExitCode != 0) throw new InvalidOperationException("dotnet restore had a non-zero exit code.");
        }

        private static async Task<MSBuildWorkspace> CreateWorkspaceAsync(Dictionary<string, string> buildProps)
        {
            if (MSBuildLocator.CanRegister) {
                // DiscoveryType.VisualStudioSetup not supported in dot net core and never will be: https://github.com/microsoft/MSBuildLocator/issues/61
                var latestMsBuildExePath = await ProcessRunner.GetSuccessStdOutAsync(@"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe", "-latest", "-prerelease", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe");
                if (latestMsBuildExePath != null) {
                    MSBuildLocator.RegisterMSBuildPath(Path.GetDirectoryName(latestMsBuildExePath));
                } else {
                    var instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
                    var instance = instances.OrderByDescending(x => x.Version).FirstOrDefault()
                        ?? throw new InvalidOperationException("No Visual Studio instance available");
                    MSBuildLocator.RegisterInstance(instance);
                }
            }
            return MSBuildWorkspace.Create(buildProps);
        }

        public void Dispose()
        {
            if (_cachedSolution != null) _cachedSolution.Workspace.Dispose();
        }
    }
}