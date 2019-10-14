﻿// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABILITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Analysis;
using Microsoft.Python.Analysis.Analyzer;
using Microsoft.Python.Analysis.Analyzer.Expressions;
using Microsoft.Python.Analysis.Core.Interpreter;
using Microsoft.Python.Analysis.Diagnostics;
using Microsoft.Python.Analysis.Types;
using Microsoft.Python.Analysis.Values;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Collections;
using Microsoft.Python.Core.Text;
using Microsoft.Python.LanguageServer.Indexing;
using Microsoft.Python.LanguageServer.Protocol;
using Microsoft.Python.Parsing.Ast;
using Range = Microsoft.Python.Core.Text.Range;

namespace Microsoft.Python.LanguageServer.CodeActions {
    internal sealed class MissingImportCodeActionProvider : ICodeActionProvider {
        private const int ModuleLoadTimeout = 10 * 1000; // 10 seconds

        public static readonly ICodeActionProvider Instance = new MissingImportCodeActionProvider();

        private MissingImportCodeActionProvider() {
        }

        public ImmutableArray<string> FixableDiagnostics => ImmutableArray<string>.Create(
            ErrorCodes.UndefinedVariable, ErrorCodes.VariableNotDefinedGlobally, ErrorCodes.VariableNotDefinedNonLocal);

        public async Task<IEnumerable<CodeAction>> GetCodeActionAsync(IDocumentAnalysis analysis, DiagnosticsEntry diagnostic, CancellationToken cancellationToken) {
            // * TODO * for now, complete. since I don't know what each option mean. 
            var finder = new ExpressionFinder(analysis.Ast, FindExpressionOptions.Complete);

            // * TODO * need to check whether it is expected node kind or add code context check to verify it is a place where module/type can appear
            var node = finder.GetExpression(diagnostic.SourceSpan);
            if (!(node is NameExpression)) {
                return Enumerable.Empty<CodeAction>();
            }

            var interpreter = analysis.Document.Interpreter;
            var pathResolver = interpreter.ModuleResolution.CurrentPathResolver;

            var name = node.ToCodeString(analysis.Ast);
            if (string.IsNullOrEmpty(name)) {
                return Enumerable.Empty<CodeAction>();
            }

            // find path to module that might have symbols for us
            await EnsureCandidateModulesAsync(analysis, name, cancellationToken);

            var fullyQualifiedNames = new HashSet<string>();

            // find modules matching the given name. this will include submodules
            var languageVersion = Parsing.PythonLanguageVersionExtensions.ToVersion(interpreter.LanguageVersion);
            var includeImplicit = !ModulePath.PythonVersionRequiresInitPyFiles(languageVersion);
            fullyQualifiedNames.UnionWith(pathResolver.GetAllImportableModulesByName(name, includeImplicit));

            // find members matching the given name from module already imported.
            var visited = new HashSet<IPythonModule>();
            var nameParts = new List<string>();
            foreach (var module in interpreter.ModuleResolution.GetImportedModules(cancellationToken)) {
                nameParts.Add(module.Name);
                CollectCandidates(module, name, visited, nameParts, fullyQualifiedNames, cancellationToken);
                nameParts.RemoveAt(nameParts.Count - 1);
            }

            var codeActions = new List<CodeAction>();
            var localCodeActions = new List<CodeAction>();
            foreach (var fullyQualifiedName in fullyQualifiedNames.OrderBy(n => n, ModuleNameComparer.Instance)) {
                cancellationToken.ThrowIfCancellationRequested();

                codeActions.AddIfNotNull(CreateCodeAction(analysis, node, fullyQualifiedName, locallyInserted: false, cancellationToken));
                localCodeActions.AddIfNotNull(CreateCodeAction(analysis, node, fullyQualifiedName, locallyInserted: true, cancellationToken));
            }

            return codeActions.Concat(localCodeActions);
        }

        private static async Task EnsureCandidateModulesAsync(IDocumentAnalysis analysis,
                                                              string name,
                                                              CancellationToken cancellationToken) {
            var indexManager = analysis.ExpressionEvaluator.Services.GetService<IIndexManager>();
            if (indexManager == null) {
                // indexing is not supported
                return;
            }

            var symbolsIncludingName = await indexManager.WorkspaceSymbolsAsync(name, maxLength: int.MaxValue, includeLibraries: true, cancellationToken);
            var symbolsWithName = symbolsIncludingName.Where(s => s.Name == name && s.Kind != Indexing.SymbolKind.Variable);

            var analyzer = analysis.ExpressionEvaluator.Services.GetService<IPythonAnalyzer>();
            var pathResolver = analysis.Document.Interpreter.ModuleResolution.CurrentPathResolver;

            var modules = ImmutableArray<IPythonModule>.Empty;
            foreach (var moduleName in symbolsWithName.Select(s => s.DocumentPath).Distinct().Select(p => pathResolver.GetModuleNameByPath(p))) {
                var module = analysis.Document.Interpreter.ModuleResolution.GetOrLoadModule(moduleName);
                if (module.Analysis is EmptyAnalysis) {
                    // once module is analyzed, this analysis result will be kept in memory
                    // even if graph for current document (modified) changed.
                    // so this will let us to analyze these modules only once
                    modules = modules.Add(module);
                }
            }

            if (modules.Count > 0) {
                // declares given modules to be dependent to current document. otherwise, our optimization will drop
                // analyzing these modules. this dependency will go away when user change the document
                analyzer.EnqueueDocumentForAnalysis(analysis.Document, modules);

                // this will make those modules to be loaded. we don't care about return analysis since we only
                // care those modules being loaded and analyzed. but not new current document's analysis
                await analysis.Document.GetAnalysisAsync(ModuleLoadTimeout, cancellationToken);
            }
        }

        private CodeAction CreateCodeAction(IDocumentAnalysis analysis,
                                            Node node,
                                            string fullyQualifiedName,
                                            bool locallyInserted,
                                            CancellationToken cancellationToken) {
            var insertionPoint = GetInsertionInfo(analysis, node, fullyQualifiedName, locallyInserted, cancellationToken);
            if (insertionPoint == null) {
                return null;
            }

            var insertionText = insertionPoint.Value.InsertionText;
            var titleText = locallyInserted ? string.Format(Resources.ImportLocally, insertionText) : insertionText;

            var sb = new StringBuilder();
            sb.AppendIf(insertionPoint.Value.Range.start == insertionPoint.Value.Range.end, insertionPoint.Value.Indentation);
            sb.Append(insertionPoint.Value.AddBlankLine ? insertionText + Environment.NewLine : insertionText);
            sb.AppendIf(insertionPoint.Value.Range.start == insertionPoint.Value.Range.end, Environment.NewLine);

            var changes = new Dictionary<Uri, TextEdit[]> {{
                        analysis.Document.Uri,
                        new TextEdit[] {
                            new TextEdit() {
                                range = insertionPoint.Value.Range,
                                newText = sb.ToString()
                        }}
                }};

            return new CodeAction() {
                title = titleText,
                edit = new WorkspaceEdit() {
                    changes = changes
                }
            };
        }

        private InsertionInfo? GetInsertionInfo(IDocumentAnalysis analysis,
                                                Node node,
                                                string fullyQualifiedName,
                                                bool locallyInserted,
                                                CancellationToken cancellationToken) {
            var (body, indentation) = GetStartingPoint(analysis, node, locallyInserted, cancellationToken);
            if (body == null) {
                // no insertion point
                return null;
            }

            var importNodes = body.GetChildNodes().Where(c => c is ImportStatement || c is FromImportStatement).ToList();
            var lastImportNode = importNodes.LastOrDefault();

            // first check whether module name is dotted or not
            var dotIndex = fullyQualifiedName.LastIndexOf('.');
            if (dotIndex < 0) {
                // there can't be existing import since we have the error
                return new InsertionInfo(addBlankLine: lastImportNode == null,
                                         $"import {fullyQualifiedName}",
                                         GetRange(analysis.Ast, body, lastImportNode),
                                         indentation);
            }

            // see whether there is existing from * import * statement.
            var fromPart = fullyQualifiedName.Substring(startIndex: 0, dotIndex);
            var nameToAdd = fullyQualifiedName.Substring(dotIndex + 1);
            foreach (var current in importNodes.Reverse<Node>().OfType<FromImportStatement>()) {
                if (current.Root.MakeString() == fromPart) {
                    return new InsertionInfo(addBlankLine: false,
                                             GetInsertionText(current, fromPart, nameToAdd),
                                             current.GetSpan(analysis.Ast),
                                             indentation);
                }
            }

            // add new from * import * statement
            return new InsertionInfo(addBlankLine: lastImportNode == null,
                                     $"from {fromPart} import {nameToAdd}",
                                     GetRange(analysis.Ast, body, lastImportNode),
                                     indentation);
        }

        private string GetInsertionText(FromImportStatement fromImportStatement, string rootModuleName, string moduleNameToAdd) {
            var imports = fromImportStatement.Names.Select(n => n.Name)
                .Concat(new string[] { moduleNameToAdd })
                .OrderBy(n => n).ToList();
            return $"from {rootModuleName} import {string.Join(", ", imports)}";
        }

        private Range GetRange(PythonAst ast, Statement body, Node lastImportNode) {
            var position = GetPosition(ast, body, lastImportNode);
            return new Range() { start = position, end = position };
        }

        private Position GetPosition(PythonAst ast, Statement body, Node lastImportNode) {
            if (lastImportNode != null) {
                var endLocation = lastImportNode.GetEnd(ast);
                return new Position { line = endLocation.Line, character = 0 };
            }

            // firstNode must exist in this context
            var firstNode = body.GetChildNodes().First();
            return new Position() { line = firstNode.GetStart(ast).Line - 1, character = 0 };
        }

        private (Statement body, string indentation) GetStartingPoint(IDocumentAnalysis analysis,
                                                                      Node node,
                                                                      bool locallyInserted,
                                                                      CancellationToken cancellationToken) {
            if (!locallyInserted) {
                return (analysis.Ast.Body, string.Empty);
            }

            var candidate = GetParents(analysis.Ast.Body, node, cancellationToken).Where(p => p is FunctionDefinition).LastOrDefault();

            // for now, only stop at FunctionDefinition. 
            // we can expand it to more scope if we want but this seems what other tool also provide as well.
            // this will return closest scope from given node
            switch (candidate) {
                case FunctionDefinition functionDefinition:
                    return (functionDefinition.Body, GetIndentation(analysis.Ast, functionDefinition.Body));
                default:
                    // no local scope
                    return default;
            }
        }

        private string GetIndentation(PythonAst ast, Statement body) {
            // first token must exist in current context
            var firstToken = body.GetChildNodes().First();

            // not sure how to handle a case where user is using "tab" instead of "space"
            // for indentation. where can one get tab over indentation option?
            return new string(' ', firstToken.GetStart(ast).Column - 1);
        }

        private List<Node> GetParents(Node root, Node node, CancellationToken cancellationToken) {
            var parentChain = new List<Node>();

            // there seems no way to go up the parent chain. always has to go down from the top
            while (root != null) {
                cancellationToken.ThrowIfCancellationRequested();

                var temp = root;
                root = null;

                // this assumes node is not overlapped and children are ordered from left to right
                // in textual position
                foreach (var current in temp.GetChildNodes()) {
                    if (!current.IndexSpan.Contains(node.IndexSpan)) {
                        continue;
                    }

                    parentChain.Add(current);
                    root = current;
                    break;
                }
            }

            return parentChain;
        }

        private void CollectCandidates(IPythonModule module,
                                       string name,
                                       HashSet<IPythonModule> visited,
                                       List<string> nameParts,
                                       HashSet<string> fullyQualifiedNames,
                                       CancellationToken cancellationToken) {
            if (module == null || !visited.Add(module)) {
                return;
            }

            // add non module (imported) member
            AddNonImportedMemberWithName(module, name, nameParts, fullyQualifiedNames);

            // add module (imported) members if it shows up in __all__
            foreach (var memberName in GetAllVariables(module.Analysis)) {
                cancellationToken.ThrowIfCancellationRequested();

                var pythonModule = module.GetMember(memberName) as IPythonModule;
                if (pythonModule == null) {
                    continue;
                }

                nameParts.Add(memberName);
                if (string.Equals(memberName, name)) {
                    AddNameParts(nameParts, fullyQualifiedNames);
                }

                CollectCandidates(pythonModule, name, visited, nameParts, fullyQualifiedNames, cancellationToken);
                nameParts.RemoveAt(nameParts.Count - 1);
            }
        }

        private void AddNonImportedMemberWithName(IPythonModule module, string name, List<string> nameParts, HashSet<string> fullyQualifiedNames) {
            // for now, skip any protected or private member
            if (name.StartsWith("_")) {
                return;
            }

            var pythonType = module.GetMember<IPythonType>(name);
            if (pythonType == null || pythonType is IPythonModule || pythonType.IsUnknown()) {
                return;
            }

            // skip any imported member (non module member) unless it is explicitly on __all__
            if (module.Analysis.GlobalScope.Imported.TryGetVariable(name, out var imported) &&
                object.Equals(pythonType, imported.Value) &&
                GetAllVariables(module.Analysis).All(s => !string.Equals(s, name))) {
                return;
            }

            nameParts.Add(name);
            AddNameParts(nameParts, fullyQualifiedNames);
            nameParts.RemoveAt(nameParts.Count - 1);
        }

        private static bool AddNameParts(List<string> nameParts, HashSet<string> fullyQualifiedNames) {
            return fullyQualifiedNames.Add(string.Join('.', nameParts));
        }

        private IEnumerable<string> GetAllVariables(IDocumentAnalysis analysis) {
            // this is different than StartImportMemberNames since that only returns something when
            // all entries are known. for import, we are fine doing best effort
            if (analysis.GlobalScope.Variables.TryGetVariable("__all__", out var variable) &&
                variable?.Value is IPythonCollection collection) {
                return collection.Contents
                    .OfType<IPythonConstant>()
                    .Select(c => c.GetString())
                    .Where(s => !string.IsNullOrEmpty(s));
            }

            return Array.Empty<string>();
        }

        private class ModuleNameComparer : IComparer<string> {
            public static readonly ModuleNameComparer Instance = new ModuleNameComparer();

            private ModuleNameComparer() { }

            public int Compare(string x, string y) {
                // move "_" to back of the list
                if (x.StartsWith("_") && y.StartsWith("_")) {
                    return x.CompareTo(y);
                }
                if (x.StartsWith("_")) {
                    return 1;
                }
                if (y.StartsWith("_")) {
                    return -1;
                }

                return x.CompareTo(y);
            }
        }

        private struct InsertionInfo {
            public bool AddBlankLine;
            public string InsertionText;
            public Range Range;
            public string Indentation;

            public InsertionInfo(bool addBlankLine, string insertionText, Range range, string indentation) {
                AddBlankLine = addBlankLine;
                InsertionText = insertionText;
                Range = range;
                Indentation = indentation;
            }
        }
    }
}

