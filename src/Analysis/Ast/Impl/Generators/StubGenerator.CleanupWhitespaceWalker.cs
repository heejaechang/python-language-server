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
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.Python.Analysis.Types;
using Microsoft.Python.Core.Logging;
using Microsoft.Python.Core.Text;
using Microsoft.Python.Parsing.Ast;
using Microsoft.Python.Parsing.Extensions;

namespace Microsoft.Python.Analysis.Generators {
    public sealed partial class StubGenerator {
        private sealed class CleanupWhitespaceWalker : BaseWalker {
            // we could push actual indentation string rather than indentation size
            // to workaround "space" vs "tab" option which we don't have an access to in
            // this code path
            private readonly LinkedList<(int start, int indentation)> _indentations;
            private readonly Stack<int> _indentationStack;

            public CleanupWhitespaceWalker(ILogger logger, IPythonModule module, PythonAst ast, string original, CancellationToken cancellationToken)
                : base(logger, module, ast, original, cancellationToken) {
                // it would be nice if there is a general formatter one can use to just format code rather than
                // having this kind of custom walker for feature which does simple formatting
                _indentations = new LinkedList<(int start, int indentation)>();
                _indentationStack = new Stack<int>();
            }

            public override bool Walk(ClassDefinition node, Node parent) {
                PushIndentation(node, node.HeaderIndex);
                return true;
            }

            public override void PostWalk(ClassDefinition node, Node parent) {
                PopIndentation(node, parent);
            }

            public override bool Walk(FunctionDefinition node, Node parent) {
                PushIndentation(node, node.HeaderIndex);

                // assumes no nested function. no need to walk down
                return false;
            }

            public override void PostWalk(FunctionDefinition node, Node parent) {
                PopIndentation(node, parent);
            }

            public override string GetCode() {
                // get flat statement list
                var statements = Ast.ChildNodesDepthFirst().Where(Candidate).ToList();

                // very first statement
                var firstStatement = statements[0];
                ReplaceNodeWithText(
                    GetSpacesBetween(previous: null, firstStatement, indentation: GetIndentation(firstStatement)),
                    GetSpan(previous: null, firstStatement));

                for (var i = 1; i < statements.Count; i++) {
                    CancellationToken.ThrowIfCancellationRequested();

                    var previous = statements[i - 1];
                    var current = statements[i];

                    var span = GetSpan(previous, current);
                    var codeBetweenStatement = GetOriginalText(span);
                    if (!string.IsNullOrWhiteSpace(codeBetweenStatement)) {
                        AppendOriginalText(current.StartIndex);
                        continue;
                    }

                    var indentation = GetIndentation(current);
                    if (indentation < 0) {
                        // previous and current is on same line
                        AppendOriginalText(current.StartIndex);
                        continue;
                    }

                    var spacesBetween = GetSpacesBetween(previous, current, indentation);
                    ReplaceNodeWithText(spacesBetween, span);
                }

                return base.GetCode();

                bool Candidate(Node node) {
                    switch (node) {
                        case PythonAst _:
                            return false;
                        case SuiteStatement _:
                            return false;
                        case Statement _:
                            return true;
                        default:
                            return false;
                    }
                }
            }

            private static IndexSpan GetSpan(Node previous, Node current) {
                if (previous == null) {
                    return IndexSpan.FromBounds(0, current.StartIndex);
                }

                // previous could contain current (ex, class definition)
                // in that case, break span from previous.Start to current.start
                // rather than end
                if (previous.IndexSpan.Contains(current.IndexSpan)) {
                    return IndexSpan.FromBounds(GetEndIndex(previous), current.StartIndex);
                }

                return IndexSpan.FromBounds(previous.EndIndex, current.StartIndex);

                int GetEndIndex(Node node) {
                    if (node is ClassDefinition @class) {
                        return @class.HeaderIndex + 1;
                    }

                    if (node is FunctionDefinition func) {
                        return func.HeaderIndex + 1;
                    }

                    return node.EndIndex;
                }
            }

            private void PushIndentation(ScopeStatement node, int headerIndex) {
                CancellationToken.ThrowIfCancellationRequested();

                _indentationStack.Push(ComputeIndentation(node));
                _indentations.AddLast((headerIndex, _indentationStack.Peek()));
            }

            private void PopIndentation(ScopeStatement node, Node parent) {
                CancellationToken.ThrowIfCancellationRequested();

                _indentationStack.Pop();

                var indentation = _indentationStack.Count == 0 ? 0 : _indentationStack.Peek();
                _indentations.AddLast((node.EndIndex, indentation));

                Debug.Assert(indentation == ComputeIndentation(GetContainer(parent)));
            }

            private string GetSpacesBetween(Node previous, Node current, int indentation) {
                if (previous == null) {
                    return new string(' ', indentation);
                }

                // same kind of node of one liner
                if (previous.NodeName == current.NodeName && OnSingleLine(previous) && OnSingleLine(current)) {
                    return Environment.NewLine + new string(' ', indentation);
                }

                // no line between header and doc comment
                if (IsDocumentation(current as Statement)) {
                    return Environment.NewLine + new string(' ', indentation);
                }

                // different kind of node or multiple lines
                // always has 1 blank line between
                //
                // all tab vs space option is wrong. not sure how to get option and it probably is job to formatter
                return Environment.NewLine + Environment.NewLine + new string(' ', indentation);
            }

            private bool OnSingleLine(Node previous) {
                var span = previous.GetSpan(Ast);
                return span.Start.Line == span.End.Line;
            }

            private int ComputeIndentation(ScopeStatement node) {
                if (OnSingleLine(node)) {
                    // if whole class i on single line. 
                    // indentation doesn't matter
                    return -1;
                }

                var suiteStatement = node.Body as SuiteStatement;
                if (suiteStatement == null && suiteStatement.Statements.Count <= 0) {
                    // no member, we don't care indentation
                    return -1;
                }

                var start = suiteStatement.Statements[0].GetStart(Ast);
                return Math.Max(0, start.Column - 1);
            }

            private int GetIndentation(Node statement) {
                if (_indentations.Count == 0) {
                    return 0;
                }

                var current = _indentations.First;
                if (statement.StartIndex < current.Value.start) {
                    // top level
                    return 0;
                }

                // current < statement.start
                var next = current.Next;
                if (next == null || statement.StartIndex < next.Value.start) {
                    return current.Value.indentation;
                }

                // next <= statement.start
                _indentations.RemoveFirst();
                return GetIndentation(statement);
            }
        }
    }
}
