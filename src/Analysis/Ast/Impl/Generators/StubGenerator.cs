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
using System.Text;
using System.Threading;
using Microsoft.Python.Analysis.Types;
using Microsoft.Python.Core;
using Microsoft.Python.Core.IO;
using Microsoft.Python.Core.Logging;

namespace Microsoft.Python.Analysis.Generators {
    /// <summary>
    /// Generate stub (pyi) content from analyzed module using our analysis engine or
    /// Scrape stub content from scrape_module.py
    /// </summary>
    public sealed class StubGenerator {

        private readonly IPythonInterpreter _interpreter;
        private readonly IPythonModule _module;
        private readonly ILogger _logger;

        public static string Scrape(IPythonInterpreter interpreter,
                                      ILogger logger,
                                      IPythonModule module,
                                      string[] extraScrapeArguments,
                                      CancellationToken cancellationToken) {
            var defaultArgs = GetDefaultScrapeArguments();
            if (defaultArgs == null) {
                return string.Empty;
            }

            var args = defaultArgs.Concat(extraScrapeArguments ?? Enumerable.Empty<string>()).ToArray();
            return new StubGenerator(interpreter, logger, module).Generate(args, cancellationToken);
        }

        private StubGenerator(IPythonInterpreter interpreter, ILogger logger, IPythonModule module) {
            _interpreter = interpreter;
            _logger = logger;
            _module = module;
        }

        private string Generate(string[] args, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();

            var startInfo = new ProcessStartInfo {
                FileName = _interpreter.Configuration.InterpreterPath,
                Arguments = args.AsQuotedArguments(),
                WorkingDirectory = _interpreter.Configuration.LibraryPath,
                UseShellExecute = false,
                ErrorDialog = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _logger?.Log(TraceEventType.Verbose, "Scrape", startInfo.FileName, startInfo.Arguments);
            var output = string.Empty;

            try {
                using (var process = new Process()) {
                    process.StartInfo = startInfo;
                    process.ErrorDataReceived += (s, e) => { };

                    process.Start();
                    process.BeginErrorReadLine();

                    output = process.StandardOutput.ReadToEnd();
                }
            } catch (Exception ex) when (!ex.IsCriticalException()) {
                _logger?.Log(TraceEventType.Verbose, "Exception scraping module", _module.Name, ex.Message);
            }

            return output;
        }

        private static List<string> GetDefaultScrapeArguments() {
            var args = new List<string> { "-W", "ignore", "-B", "-E" };

            if (!InstallPath.TryGetFile("scrape_module.py", out var sm)) {
                return null;
            }

            args.Add(sm);
            return args;
        }
    }
}
