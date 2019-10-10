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


using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Python.Analysis;
using Microsoft.Python.Analysis.Analyzer;
using Microsoft.Python.Analysis.Diagnostics;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Collections;
using Microsoft.Python.LanguageServer.CodeActions;
using Microsoft.Python.LanguageServer.Protocol;
using Microsoft.Python.Parsing.Ast;
using Range = Microsoft.Python.Core.Text.Range;

namespace Microsoft.Python.LanguageServer.Sources {
    internal sealed partial class CodeActionSource {
        private static readonly ImmutableArray<ICodeActionProvider> _codeActionProviders =
            ImmutableArray<ICodeActionProvider>.Create(MissingImportCodeActionProvider.Instance);

        private readonly IServiceContainer _services;

        public CodeActionSource(IServiceContainer services) {
            _services = services;
        }

        public async Task<CodeAction[]> GetCodeActionsAsync(IDocumentAnalysis analysis, Diagnostic[] diagnostics, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();

            var results = new List<CodeAction>();

            // * NOTE * this always re-calculate whole document for all linters.
            //          consider creating linter result cache somewhere (MRU?) and 
            //          add error code as optional argument to run only that linter rather
            //          than always running all linters.
            //          also, current LintModule implementation always run all linters
            //          even if the linter option is off to set references in the given
            //          modules. this code path should use different code path to prevent that.
            foreach (var diagnostic in GetMatchingDiagnostics(analysis, diagnostics, cancellationToken)) {
                foreach (var codeActionProvider in _codeActionProviders) {
                    if (codeActionProvider.FixableDiagnostics.Any(code => code == diagnostic.ErrorCode)) {
                        results.AddRange(await codeActionProvider.GetCodeActionAsync(analysis, diagnostic, cancellationToken));
                    }
                }
            }

            return results.ToArray();
        }

        private IEnumerable<DiagnosticsEntry> GetMatchingDiagnostics(IDocumentAnalysis analysis, Diagnostic[] diagnostics, CancellationToken cancellationToken) {
            var analyzer = _services.GetService<IPythonAnalyzer>();
            foreach (var diagnostic in analysis.Diagnostics.Concat(analyzer.LintModule(analysis.Document))) {
                cancellationToken.ThrowIfCancellationRequested();

                if (diagnostics.Any(d => AreEqual(d, diagnostic))) {
                    yield return diagnostic;
                }
            }

            bool AreEqual(Diagnostic diagnostic1, DiagnosticsEntry diagnostic2) {
                return diagnostic1.code == diagnostic2.ErrorCode &&
                       diagnostic1.range.ToSourceSpan() == diagnostic2.SourceSpan;
            }
        }
    }
}
