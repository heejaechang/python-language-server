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
using Microsoft.Python.Analysis.Types;
using Microsoft.Python.Core.Logging;
using Microsoft.Python.Parsing.Ast;

namespace Microsoft.Python.Analysis.Generators {
    public sealed partial class StubGenerator {
        private sealed class RemovePrivateMemberWalker : BaseWalker {
            private readonly HashSet<string> _allVariables;

            public RemovePrivateMemberWalker(ILogger logger, IPythonModule module, PythonAst ast, HashSet<string> allVariables, string original)
                : base(logger, module, ast, original) {
                _allVariables = allVariables;
            }

            public override bool Walk(FunctionDefinition node, Node parent) {
                if (IsPrivate(node.Name, _allVariables)) {
                    // remove private member not in __all__
                    return RemoveNode(node.IndexSpan);
                }

                return base.Walk(node, parent);
            }

            public override bool Walk(AssignmentStatement node, Node parent) {
                if (node.Left.Count == 1 && node.Left[0] is NameExpression nex) {
                    if (nex.Name == "__doc__" && node.Right is ConstantExpression constant && constant.GetStringValue() != null) {
                        // don't remove doc string for module if it exist
                        return false;
                    }

                    if (IsPrivate(nex.Name, _allVariables) && !UsedInModule(nex.Name)) {
                        // remove any private variables
                        return RemoveNode(node.IndexSpan);
                    }
                }

                return base.Walk(node, parent);
            }

            private bool UsedInModule(string name) {
                // the one we generated (_mod_xxxx), for now, we always say "not used" and remove those for now
                // for as lvalue in assignment
                if (name.StartsWith("_mod_")) {
                    return false;
                }

                // otherwise, check existing analysis of original code
                var eval = Module.Analysis.ExpressionEvaluator;
                var member = eval.LookupNameInScopes(name, Analyzer.LookupOptions.All);
                if (member == null || member.IsUnknown()) {
                    return false;
                }

                // TODO: we need to see references to this and whether it ends up used in a call to define public variable
                //       in that case, we can't remove this private variable
                return false;
            }
        }
    }
}
