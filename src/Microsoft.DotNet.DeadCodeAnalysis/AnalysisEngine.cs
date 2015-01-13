﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.DotNet.DeadCodeAnalysis
{
    public class AnalysisEngine
    {
        private AnalysisOptions m_options;

        private IList<Project> m_projects;

        private HashSet<string> m_ignoredSymbols;

        public Workspace Workspace { get { return m_projects[0].Solution.Workspace; } }

        public AnalysisEngine(AnalysisOptions options)
        {
            m_options = options;
            m_ignoredSymbols = new HashSet<string>(m_options.AlwaysIgnoredSymbols);
        }

        public async Task RunAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            var regionInfo = await GetConditionalRegionInfo(cancellationToken);

            if (m_options.Edit)
            {
                await CleanUp.RemoveUnnecessaryRegions(m_projects[0].Solution.Workspace, regionInfo, cancellationToken);
            }

            PrintConditionalRegionInfo(regionInfo);
        }

        private void PrintConditionalRegionInfo(IEnumerable<DocumentConditionalRegionInfo> regionInfo)
        {
            var originalForegroundColor = Console.ForegroundColor;

            int disabledCount = 0;
            int enabledCount = 0;
            int varyingCount = 0;
            int explicitlyVaryingCount = 0;

            foreach (var info in regionInfo)
            {
                foreach (var chain in info.Chains)
                {
                    foreach (var region in chain.Regions)
                    {
                        switch (region.State)
                        {
                            case ConditionalRegionState.AlwaysDisabled:
                                disabledCount++;
                                Console.ForegroundColor = ConsoleColor.Blue;
                                if (m_options.PrintDisabled)
                                {
                                    Console.WriteLine(region);
                                }
                                break;
                            case ConditionalRegionState.AlwaysEnabled:
                                enabledCount++;
                                Console.ForegroundColor = ConsoleColor.Green;
                                if (m_options.PrintEnabled)
                                {
                                    Console.WriteLine(region);
                                }
                                break;
                            case ConditionalRegionState.Varying:
                                varyingCount++;
                                if (region.ExplicitlyVaries)
                                {
                                    explicitlyVaryingCount++;
                                }
                                Console.ForegroundColor = ConsoleColor.DarkGray;
                                if (m_options.PrintVarying)
                                {
                                    Console.WriteLine(region);
                                }
                                break;
                        }
                    }
                }
            }

            Console.ForegroundColor = originalForegroundColor;

            // Print summary
            Console.WriteLine();

            int totalRegionCount = disabledCount + enabledCount + varyingCount;
            if (totalRegionCount == 0)
            {
                Console.WriteLine("Did not find any conditional regions.");
            }

            Console.WriteLine("Found");
            Console.WriteLine("  {0,5} conditional regions total", totalRegionCount);

            string alwaysString = m_projects.Count > 1 ? "always " : string.Empty;

            if (disabledCount > 0)
            {
                Console.WriteLine("  {0,5} {1}disabled", disabledCount, alwaysString);
            }

            if (enabledCount > 0)
            {
                Console.WriteLine("  {0,5} {1}enabled", enabledCount, alwaysString);
            }

            if (varyingCount > 0)
            {
                Console.WriteLine("  {0,5} varying", varyingCount);
                Console.WriteLine("    {0,5} due to real varying symbols", varyingCount - explicitlyVaryingCount);
                Console.WriteLine("    {0,5} due to ignored symbols", explicitlyVaryingCount);
            }

            // TODO: Lines of dead code.  A chain struct might be useful because there are many operations on a chain.
            // This involves calculating unnecessary regions, converting those to line spans
        }

        /// <summary>
        /// Returns the intersection of <see cref="DocumentConditionalRegionInfo"/> in the given projects.
        /// The contained <see cref="Document"/> objects will all be from the first project.
        /// </summary>
        public async Task<IList<DocumentConditionalRegionInfo>> GetConditionalRegionInfo(CancellationToken cancellationToken = default(CancellationToken))
        {
            if (m_options.ProjectPaths != null)
            {
                m_projects = await Task.WhenAll(from path in m_options.ProjectPaths select MSBuildWorkspace.Create().OpenProjectAsync(path, cancellationToken));
            }
            else if (m_options.Sources != null)
            {
                m_projects = new[] { CreateProject(m_options.Sources.ToArray()) };
            }

            if (m_projects.Count == 1)
            {
                return await GetConditionalRegionInfo(m_projects[0], d => true, cancellationToken);
            }

            // Intersect the set of files in the projects so that we only analyze the set of files shared between all projects
            var filePaths = m_projects[0].Documents.Select(d => d.FilePath);

            for (int i = 1; i < m_projects.Count; i++)
            {
                filePaths = filePaths.Intersect(
                    m_projects[i].Documents.Select(d => d.FilePath),
                    StringComparer.InvariantCultureIgnoreCase);
            }

            var filePathSet = new HashSet<string>(filePaths);
            Predicate<Document> shouldAnalyzeDocument = doc => filePathSet.Contains(doc.FilePath);

            // Intersect the conditional regions of each document shared between all the projects
            IList<DocumentConditionalRegionInfo> infoA = await GetConditionalRegionInfo(m_projects[0], shouldAnalyzeDocument, cancellationToken);

            for (int i = 1; i < m_projects.Count; i++)
            {
                var infoB = await GetConditionalRegionInfo(m_projects[i], shouldAnalyzeDocument, cancellationToken);
                infoA = IntersectConditionalRegionInfo(infoA, infoB);
            }

            return infoA;
        }

        private Project CreateProject(string[] sources, string language = LanguageNames.CSharp)
        {
            string projectName = "GeneratedProject";
            string fileExtension = language == LanguageNames.CSharp ? ".cs" : ".vb";
            var projectId = ProjectId.CreateNewId(projectName);

            var solution = new CustomWorkspace()
                .CurrentSolution
                .AddProject(projectId, projectName, projectName, language);

            // TODO: Preprocessor symbols = AlwaysDefined - AlwaysDisabled
            var disabledSymbols = new HashSet<string>(m_options.AlwaysDisabledSymbols);
            var preprocessorSymbols = disabledSymbols.Where(s => !disabledSymbols.Contains(s));

            var project = solution.Projects.Single();
            project = project.WithParseOptions(
                ((CSharpParseOptions)project.ParseOptions).WithPreprocessorSymbols(preprocessorSymbols));

            solution = project.Solution;

            string fileNamePrefix = "source";
            int count = 0;
            foreach (var source in sources)
            {
                var fileName = fileNamePrefix + count++ + fileExtension;
                var documentId = DocumentId.CreateNewId(projectId, fileName);
                solution = solution.AddDocument(documentId, fileName, SourceText.From(source));
            }

            return solution.GetProject(projectId);
        }

        /// <summary>
        /// Returns a sorted array of <see cref="DocumentConditionalRegionInfo"/> for the specified project, filtered by the given predicate.
        /// </summary>
        private async Task<DocumentConditionalRegionInfo[]> GetConditionalRegionInfo(Project project, Predicate<Document> predicate, CancellationToken cancellationToken)
        {
            var documentInfos = await Task.WhenAll(
                from document in project.Documents
                where predicate(document)
                select GetConditionalRegionInfo(document, cancellationToken));

            Array.Sort(documentInfos);

            return documentInfos;
        }

        private async Task<DocumentConditionalRegionInfo> GetConditionalRegionInfo(Document document, CancellationToken cancellationToken)
        {
            var chains = new List<ConditionalRegionChain>();
            var regions = new List<ConditionalRegion>();

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken) as CSharpSyntaxTree;
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            if (root.ContainsDirectives)
            {
                var currentDirective = root.GetFirstDirective(IsBranchingDirective);
                var visitedDirectives = new HashSet<DirectiveTriviaSyntax>();

                while (currentDirective != null)
                {
                    var chain = ParseConditionalRegionChain(currentDirective.GetLinkedDirectives(), visitedDirectives);
                    if (chain != null)
                    {
                        chains.Add(new ConditionalRegionChain(chain));
                    }

                    do
                    {
                        currentDirective = currentDirective.GetNextDirective(IsBranchingDirective);
                    } while (visitedDirectives.Contains(currentDirective));
                }
            }

            return new DocumentConditionalRegionInfo(document, chains);
        }

        private List<ConditionalRegion> ParseConditionalRegionChain(List<DirectiveTriviaSyntax> directives, HashSet<DirectiveTriviaSyntax> visitedDirectives)
        {
            DirectiveTriviaSyntax previousDirective = null;
            var chain = new List<ConditionalRegion>();
            bool explicitlyVaries = false;

            for (int i = 0; i < directives.Count; i++)
            {
                var directive = directives[i];

                if (visitedDirectives.Contains(directive))
                {
                    // We've already visited this chain of linked directives
                    return null;
                }

                if (previousDirective != null)
                {
                    // Ignore chains with inactive directives because their conditions are not evaluated by the parser.
                    if (!previousDirective.IsActive)
                    {
                        return null;
                    }

                    // If a condition has been specified as explicitly varying, then all following conditions
                    // are implicitly varying because each successive directive depends on the condition of
                    // the preceding directive.
                    if (!explicitlyVaries && DependsOnIgnoredSymbols(previousDirective))
                    {
                        explicitlyVaries = true;
                    }

                    var region = new ConditionalRegion(previousDirective, directive, chain, chain.Count, explicitlyVaries);
                    chain.Add(region);
                }

                previousDirective = directive;
                visitedDirectives.Add(directive);
            }

            return chain;
        }

        private bool DependsOnIgnoredSymbols(DirectiveTriviaSyntax directive)
        {
            ExpressionSyntax condition = null;

            switch (directive.CSharpKind())
            {
                case SyntaxKind.IfDirectiveTrivia:
                    condition = ((IfDirectiveTriviaSyntax)directive).Condition;
                    break;
                case SyntaxKind.ElifDirectiveTrivia:
                    condition = ((ElifDirectiveTriviaSyntax)directive).Condition;
                    break;
                case SyntaxKind.ElseDirectiveTrivia:
                case SyntaxKind.EndIfDirectiveTrivia:
                    // #endif directives don't have expressions, so they can't depend on ignored symbols.
                    // If an #else directive depends on an ignored symbol, we will have caught that earlier
                    // when looking at the corresponding #if directive.
                    return false;
                default:
                    Debug.Assert(false);
                    return false;
            }

            foreach (var child in condition.DescendantNodesAndSelf())
            {
                var identifier = child as IdentifierNameSyntax;
                if (identifier != null)
                {
                    if (m_ignoredSymbols.Contains(identifier.Identifier.ValueText))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsBranchingDirective(DirectiveTriviaSyntax directive)
        {
            switch (directive.CSharpKind())
            {
                case SyntaxKind.IfDirectiveTrivia:
                case SyntaxKind.ElifDirectiveTrivia:
                case SyntaxKind.ElseDirectiveTrivia:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Intersects two arrays of <see cref="DocumentConditionalRegionInfo"/> for the same document.
        /// The data contained in <param name="x"/> will be modified.
        /// Note that both <param name="x"/> and <param name="y"/> are assumed to be sorted.
        /// </summary>
        private static IList<DocumentConditionalRegionInfo> IntersectConditionalRegionInfo(IList<DocumentConditionalRegionInfo> x, IList<DocumentConditionalRegionInfo> y)
        {
            var info = new List<DocumentConditionalRegionInfo>();
            int i = 0;
            int j = 0;

            while (i < x.Count && j < y.Count)
            {
                var result = x[i].CompareTo(y[j]);

                if (result == 0)
                {
                    x[i].Intersect(y[j]);
                    info.Add(x[i]);

                    i++;
                    j++;
                }
                else if (result < 0)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            return info;
        }
    }
}
