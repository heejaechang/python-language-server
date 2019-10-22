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
using Microsoft.Python.Analysis.Diagnostics;
using Microsoft.Python.Analysis.Types;
using Microsoft.Python.Analysis.Values;
using Microsoft.Python.Core;
using Microsoft.Python.Core.Collections;

namespace Microsoft.Python.Analysis.Linting.UndefinedVariables {
    internal sealed class UnusedImportsLinter : ILinter {
        public IReadOnlyList<DiagnosticsEntry> Lint(IDocumentAnalysis analysis, IServiceContainer services) {
            var result = ImmutableArray<DiagnosticsEntry>.Empty;

            var imported = analysis.GlobalScope.Imported;

            // * NOTE * variable declared in imported is different than same variable referenced in the code.
            //          that is because that variable is re-declared in another variable collection
            //          not sure whether it is a bug or intentional behavior. might be intentional to distinguish
            //          same name used for 2 different varialbes. need to check
            var variableDeclared = analysis.GlobalScope.Variables;
            foreach (var name in imported.Names) {
                if (!imported.TryGetVariable(name, out var variableFromImportCollection)) {
                    continue;
                }

                // we have variable from import statement, but we don't have any variable declared from actual
                // usage. meaning the import is not used.
                if (!variableDeclared.TryGetVariable(name, out var variableFromVariableCollection)) {
                    ReportUnusedImports(variableFromImportCollection, ref result);
                    continue;
                }

                // * NOTE * this seems won't work if variable with same name declared multiple times?
                if (!LocationInfo.FullComparer.Equals(variableFromVariableCollection.Definition, variableFromImportCollection.Definition)) {
                    continue;
                }

                // find any reference in current file which is not the import variable definition itself
                // we need to use one from variable declared collection since FAR info is only there
                if (variableFromVariableCollection.References.Any(r => r.DocumentUri == variableFromImportCollection.Definition.DocumentUri &&
                                                                       r.Span != variableFromImportCollection.Definition.Span)) {
                    continue;
                }

                ReportUnusedImports(variableFromImportCollection, ref result);
            }

            return result;
        }

        private static void ReportUnusedImports(IVariable variable, ref ImmutableArray<DiagnosticsEntry> result) {
            var message = Resources._0_1_is_declared_but_it_is_never_used_within_the_current_file.FormatInvariant(variable.Value.MemberType, variable.Name);
            result = result.Add(new DiagnosticsEntry(message, variable.Definition.Span, ErrorCodes.UnusedImport, Parsing.Severity.Hint, DiagnosticSource.Linter));
        }
    }
}
