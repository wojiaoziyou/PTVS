﻿// Python Tools for Visual Studio
// Copyright(c) Microsoft Corporation
// All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the License); you may not use
// this file except in compliance with the License. You may obtain a copy of the
// License at http://www.apache.org/licenses/LICENSE-2.0
//
// THIS CODE IS PROVIDED ON AN  *AS IS* BASIS, WITHOUT WARRANTIES OR CONDITIONS
// OF ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING WITHOUT LIMITATION ANY
// IMPLIED WARRANTIES OR CONDITIONS OF TITLE, FITNESS FOR A PARTICULAR PURPOSE,
// MERCHANTABLITY OR NON-INFRINGEMENT.
//
// See the Apache Version 2.0 License for specific language governing
// permissions and limitations under the License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.PythonTools.Analysis.Analyzer;
using Microsoft.PythonTools.Analysis.Infrastructure;
using Microsoft.PythonTools.Intellisense;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.PythonTools.Parsing.Ast;

namespace Microsoft.PythonTools.Analysis.LanguageServer {
    public sealed class Server : ServerBase, IDisposable {
        internal readonly AnalysisQueue _queue;
        internal readonly ConcurrentDictionary<string, IProjectEntry> _projectFiles;
        private readonly VolatileCounter _parsingInProgress;

        internal Task _loadingFromDirectory;

        internal PythonAnalyzer _analyzer;
        internal ClientCapabilities? _clientCaps;

        // If null, all files must be added manually
        private Uri _rootDir;

        public Server() {
            _queue = new AnalysisQueue();
            _projectFiles = new ConcurrentDictionary<string, IProjectEntry>();
            _parsingInProgress = new VolatileCounter();
        }

        public void Dispose() {
            _queue.Dispose();
        }

        #region Client message handling

        public async override Task<InitializeResult> Initialize(InitializeParams @params) {
            _analyzer = await CreateAnalyzer(@params.initializationOptions.interpreter);

            _clientCaps = @params.capabilities;
            var searchPaths = @params.initializationOptions.searchPaths;
            if (searchPaths != null) {
                _analyzer.SetSearchPaths(searchPaths);
            }

            if (@params.rootUri != null) {
                _rootDir = @params.rootUri;
            } else if (!string.IsNullOrEmpty(@params.rootPath)) {
                _rootDir = new Uri(@params.rootPath);
            }

            if (_rootDir != null) {
                _loadingFromDirectory = LoadFromDirectoryAsync(_rootDir);
            }

            return new InitializeResult {
                capabilities = new ServerCapabilities {
                    completionProvider = new CompletionOptions { resolveProvider = true },
                    textDocumentSync = new TextDocumentSyncOptions { openClose = true, change = TextDocumentSyncKind.Incremental }
                }
            };
        }

        public override Task Shutdown() {
            Interlocked.Exchange(ref _analyzer, null)?.Dispose();
            _projectFiles.Clear();
            return Task.CompletedTask;
        }

        public override async Task DidOpenTextDocument(DidOpenTextDocumentParams @params) {
            var entry = GetEntry(@params.textDocument.uri) as IPythonProjectEntry;
        }

        public override async Task DidCloseTextDocument(DidCloseTextDocumentParams @params) {
            var entry = GetEntry(@params.textDocument.uri);
            // No need to keep in-memory buffers now
            (entry as IDocument)?.ResetDocument(-1, -1, null);

            // Pick up any changes on disk that we didn't know about
            _queue.Enqueue(entry, AnalysisPriority.Low);
        }

        public override async Task DidChangeConfiguration(DidChangeConfigurationParams @params) {
            if (_analyzer == null) {
                LogMessage(new LogMessageEventArgs { type = MessageType.Error, message = "change configuration notification sent to uninitialized server" });
                return;
            }

            await _analyzer.ReloadModulesAsync();

            // re-analyze all of the modules when we get a new set of modules loaded...
            foreach (var entry in _analyzer.ModulesByFilename) {
                _queue.Enqueue(entry.Value.ProjectEntry, AnalysisPriority.Normal);
            }
        }

        public override async Task<CompletionList> Completion(CompletionParams @params) {
            GetAnalysis(@params.textDocument, @params.position, @params._version, out var entry, out var tree);
            var analysis = entry?.Analysis;
            if (analysis == null) {
                return new CompletionList { };
            }

            var opts = (GetMemberOptions)0;
            if (@params.context.HasValue) {
                var c = @params.context.Value;
                if (c._intersection) {
                    opts |= GetMemberOptions.IntersectMultipleResults;
                }
                if (c._statementKeywords ?? true) {
                    opts |= GetMemberOptions.IncludeStatementKeywords;
                }
                if (c._expressionKeywords ?? true) {
                    opts |= GetMemberOptions.IncludeExpressionKeywords;
                }
            } else {
                opts = GetMemberOptions.IncludeStatementKeywords | GetMemberOptions.IncludeExpressionKeywords;
            }

            IEnumerable<MemberResult> members = null;
            var exprText = @params._expr;
            Expression expr = null;
            if (!string.IsNullOrEmpty(@params._expr)) {
                members = entry.Analysis.GetMembers(expr, @params.position, opts);
            } else {
                var finder = new ExpressionFinder(entry.Tree, GetExpressionOptions.EvaluateMembers);
                expr = finder.GetExpression(@params.position) as Expression;
                if (expr != null) {
                    members = analysis.GetMembers(expr, @params.position, opts, null);
                } else {
                    members = entry.Analysis.GetAllAvailableMembers(@params.position, opts);
                }
            }


            if (@params.context?._includeAllModules ?? false) {
                var mods = _analyzer.GetModules();
                members = members?.Concat(mods) ?? mods;
            }

            if (members == null) {
                return new CompletionList { };
            }

            var filtered = members.Select(m => ToCompletionItem(m, opts));
            var filterKind = @params.context?._filterKind;
            if (filterKind.HasValue && filterKind != CompletionItemKind.None) {
                filtered = filtered.Where(m => m.kind == filterKind.Value);
            }

            return new CompletionList {
                items = filtered.ToArray()
            };
        }

        public override async Task<CompletionItem> CompletionItemResolve(CompletionItem item) {
            // TODO: Fill out missing values in item
            return item;
        }

        public override async Task<SymbolInformation[]> WorkplaceSymbols(WorkplaceSymbolParams @params) {
            var members = Enumerable.Empty<MemberResult>();
            var opts = GetMemberOptions.ExcludeBuiltins | GetMemberOptions.DeclaredOnly;

            foreach (var entry in _projectFiles) {
                members = members.Concat(
                    GetModuleVariables(entry.Value as IPythonProjectEntry, opts, @params.query)
                );
            }

            members = members.GroupBy(mr => mr.Name).Select(g => g.First());

            return members.Select(m => ToSymbolInformation(m)).ToArray();
        }

        #endregion

        public Task WaitForDirectoryScanAsync() {
            var task = _loadingFromDirectory;
            if (task == null) {
                return Task.CompletedTask;
            }
            return task;
        }

        private IProjectEntry GetEntry(TextDocumentIdentifier document) => GetEntry(document.uri);

        private IProjectEntry GetEntry(Uri documentUri) {
            if (!_projectFiles.TryGetValue(documentUri.AbsoluteUri, out IProjectEntry entry)) {
                throw new LanguageServerException(LanguageServerException.UnknownDocument, "unknown document");
            }
            return entry;
        }

        private void GetAnalysis(TextDocumentIdentifier document, Position position, int? expectedVersion, out IPythonProjectEntry entry, out PythonAst tree) {
            entry = GetEntry(document) as IPythonProjectEntry;
            if (entry == null) {
                throw new LanguageServerException(LanguageServerException.UnsupportedDocumentType, "unsupported document");
            }
            entry.GetTreeAndCookie(out tree, out var cookie);
            if (expectedVersion.HasValue && cookie is VersionCookie vc) {
                if (vc.Buffers.TryGetValue(position._buffer ?? 0, out var buffer) && expectedVersion.Value != buffer.Version) {
                    throw new LanguageServerException(LanguageServerException.MismatchedVersion, $"buffer {position._buffer ?? 0} is at version {buffer.Version}; expected {expectedVersion.Value}");
                }
                if (buffer != null) {
                    tree = buffer.Ast;
                }
            }
        }

        private async Task<PythonAnalyzer> CreateAnalyzer(PythonInitializationOptions.Interpreter interpreter) {
            IPythonInterpreterFactory factory = null;
            if (!string.IsNullOrEmpty(interpreter.assembly) && !string.IsNullOrEmpty(interpreter.typeName)) {
                try {
                    var assembly = File.Exists(interpreter.assembly) ? AssemblyName.GetAssemblyName(interpreter.assembly) : new AssemblyName(interpreter.assembly);
                    var type = Assembly.Load(assembly).GetType(interpreter.typeName, true);

                    factory = (IPythonInterpreterFactory)Activator.CreateInstance(
                        type,
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new object[] { interpreter.properties },
                        CultureInfo.CurrentCulture
                    );
                } catch (Exception ex) {
                    LogMessage(new LogMessageEventArgs { type = MessageType.Warning, message = ex.ToString() });
                }
            }

            if (factory == null) {
                Version v;
                if (!Version.TryParse(interpreter.version ?? "0.0", out v)) {
                    v = new Version();
                }
                factory = InterpreterFactoryCreator.CreateAnalysisInterpreterFactory(v);
            }

            var interp = factory.CreateInterpreter();
            if (interp == null) {
                throw new InvalidOperationException("Failed to create interpreter");
            }

            return await PythonAnalyzer.CreateAsync(factory, interp);
        }

        private IEnumerable<ModulePath> GetImportNames(string filePath) {
            ModulePath mp;
            if (ModulePath.FromBasePathAndFile_NoThrow(_rootDir.LocalPath, filePath, out mp)) {
                yield return mp;
            }

            foreach (var sp in _analyzer.GetSearchPaths()) {
                if (ModulePath.FromBasePathAndFile_NoThrow(sp, filePath, out mp)) {
                    yield return mp;
                }
            }
        }

        public Task<IProjectEntry> LoadFileAsync(Uri documentUri) {
            return AddFileAsync(documentUri, null);
        }

        public Task<IProjectEntry> LoadFileAsync(Uri documentUri, Uri fromSearchPath) {
            return AddFileAsync(documentUri, fromSearchPath);
        }

        public Task<bool> UnloadFileAsync(Uri documentUri) {
            if (_projectFiles.TryRemove(documentUri.AbsoluteUri, out var entry)) {
                _analyzer.RemoveModule(entry);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        private Task<IProjectEntry> AddFileAsync(Uri documentUri, Uri fromSearchPath) {
            IProjectEntry item = null;

            if (_projectFiles.TryGetValue(documentUri.AbsoluteUri, out item)) {
                return Task.FromResult(item);
            }

            List<string> aliases = null;

            var path = documentUri.LocalPath;
            if (ModulePath.IsPythonSourceFile(path)) {
                if (fromSearchPath == null) {
                    aliases = GetImportNames(path).Select(mp => mp.ModuleName).ToList();
                } else {
                    if (ModulePath.FromBasePathAndFile_NoThrow(fromSearchPath.LocalPath, path, out var mp)) {
                        aliases = new List<string> { mp.ModuleName };
                    }
                }
            }

            if (!aliases.Any()) {
                return null;
            }

            var reanalyzeEntries = aliases.SelectMany(a => _analyzer.GetEntriesThatImportModule(a, true)).ToArray();

            var pyItem = _analyzer.AddModule(aliases[0], path, null);
            item = pyItem;
            foreach (var a in aliases.Skip(1)) {
                _analyzer.AddModuleAlias(aliases[0], a);
            }

            if (!_projectFiles.TryAdd(documentUri.AbsoluteUri, item)) {
                if (_projectFiles.TryGetValue(documentUri.AbsoluteUri, out var actualItem)) {
                    return Task.FromResult(actualItem);
                }
                // Fallback if we race with removal - overwrite and continue
                _projectFiles[documentUri.AbsoluteUri] = item;
            }

            pyItem.OnNewAnalysis += ProjectEntry_OnNewAnalysis;

            ParseFromDisk(pyItem);

            if (reanalyzeEntries != null) {
                foreach (var entryRef in reanalyzeEntries) {
                    _queue.Enqueue(entryRef, AnalysisPriority.Low);
                }
            }

            return Task.FromResult(item);
        }

        private void ParseFromDisk(IPythonProjectEntry entry) {
            entry.BeginParsingTree();
            _parsingInProgress.Increment();
            bool enqueued = false;
            try {
                enqueued = ThreadPool.QueueUserWorkItem(ParseFromDiskWorker, entry);
            } finally {
                if (!enqueued) {
                    _parsingInProgress.Decrement();
                }
            }
        }

        private bool CreatePythonParser(Stream stream, out Parser parser, out IReadOnlyList<Diagnostic> diagnostics, ParserOptions options = null) {
            var diags = new List<Diagnostic>();
            var opts = options?.Clone() ?? new ParserOptions();
            if (opts.ErrorSink == null) {
                opts.ErrorSink = new DiagnosticsErrorSink("Python parser", diags);
            }
            opts.BindReferences = true;
            opts.ProcessComment += new DiagnosticsErrorSink("task comment", diags).ProcessTaskComment;

            parser = Parser.CreateParser(stream, _analyzer.LanguageVersion, opts);
            diagnostics = diags;
            return true;
        }

        private void ParseFromDiskWorker(object state) {
            try {
                var entry = state as IProjectEntry;
                if (entry == null) {
                    Debug.Fail("invalid type passed to ParseItemWorker");
                    return;
                }

                using (var file = PathUtils.OpenWithRetry(entry.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (file == null) {
                        (entry as IPythonProjectEntry)?.UpdateTree(null, null);
                    } else if (entry is IExternalProjectEntry extEntry) {
                        extEntry.ParseContent(new StreamReader(file, Encoding.UTF8, true, 4096, true), null);
                    } else if (entry is IPythonProjectEntry pyEntry) {
                        if (CreatePythonParser(file, out var parser, out var diagnostics)) {
                            var tree = SafeParseFile(parser);
                            pyEntry.UpdateTree(tree, null);
                            PublishDiagnostics(new PublishDiagnosticsEventArgs {
                                diagnostics = diagnostics,
                                uri = new Uri(entry.FilePath)
                            });
                        } else {
                            pyEntry.UpdateTree(null, null);
                        }
                    }
                }
                _queue.Enqueue(entry, AnalysisPriority.Normal);
            } finally {
                _parsingInProgress.Decrement();
            }
        }

        private PythonAst SafeParseFile(Parser parser) {
            try {
                return parser.ParseFile();
            } catch (BadSourceException) {
                return null;
            } catch (Exception ex) {
                LogMessage(new LogMessageEventArgs {
                    type = MessageType.Error,
                    message = ex.ToString()
                });
                return null;
            }
        }

        public async Task WaitForCompleteAnalysisAsync() {
            // Wait for all current parsing to complete
            await _parsingInProgress.WaitForZeroAsync();
            await _queue.WaitForCompleteAsync();
        }

        private void ProjectEntry_OnNewAnalysis(object sender, EventArgs e) {
            // TODO: Something useful
        }

        private async Task LoadFromDirectoryAsync(Uri rootDir) {
            foreach (var file in PathUtils.EnumerateFiles(rootDir.LocalPath, recurse: false, fullPaths: true)) {
                if (!ModulePath.IsPythonSourceFile(file)) {
                    if (ModulePath.IsPythonFile(file, true, true, true)) {
                        // TODO: Deal with scrapable files (if we need to do anything?)
                    }
                    continue;
                }

                await LoadFileAsync(new Uri(file));
            }
            foreach (var dir in PathUtils.EnumerateDirectories(rootDir.LocalPath, recurse: false, fullPaths: true)) {
                if (!ModulePath.PythonVersionRequiresInitPyFiles(_analyzer.LanguageVersion.ToVersion()) ||
                    !string.IsNullOrEmpty(ModulePath.GetPackageInitPy(dir))) {
                    await LoadFromDirectoryAsync(new Uri(dir));
                }
            }
        }


        private CompletionItem ToCompletionItem(MemberResult m, GetMemberOptions opts) {
            var res = new CompletionItem {
                label = m.Name,
                insertText = m.Completion,
                documentation = m.Documentation,
                kind = ToCompletionItemKind(m.MemberType),
                _kind = m.MemberType.ToString().ToLowerInvariant()
            };

            return res;
        }

        private CompletionItemKind ToCompletionItemKind(PythonMemberType memberType) {
            switch (memberType) {
                case PythonMemberType.Unknown: return CompletionItemKind.None;
                case PythonMemberType.Class: return CompletionItemKind.Class;
                case PythonMemberType.Instance: return CompletionItemKind.Value;
                case PythonMemberType.Delegate: return CompletionItemKind.Class;
                case PythonMemberType.DelegateInstance: return CompletionItemKind.Function;
                case PythonMemberType.Enum: return CompletionItemKind.Enum;
                case PythonMemberType.EnumInstance: return CompletionItemKind.EnumMember;
                case PythonMemberType.Function: return CompletionItemKind.Function;
                case PythonMemberType.Method: return CompletionItemKind.Method;
                case PythonMemberType.Module: return CompletionItemKind.Module;
                case PythonMemberType.Namespace: return CompletionItemKind.Module;
                case PythonMemberType.Constant: return CompletionItemKind.Constant;
                case PythonMemberType.Event: return CompletionItemKind.Event;
                case PythonMemberType.Field: return CompletionItemKind.Field;
                case PythonMemberType.Property: return CompletionItemKind.Property;
                case PythonMemberType.Multiple: return CompletionItemKind.Value;
                case PythonMemberType.Keyword: return CompletionItemKind.Keyword;
                case PythonMemberType.CodeSnippet: return CompletionItemKind.Snippet;
                case PythonMemberType.NamedArgument: return CompletionItemKind.Variable;
                default:
                    return CompletionItemKind.None;
            }
        }

        private SymbolInformation ToSymbolInformation(MemberResult m) {
            var res = new SymbolInformation {
                name = m.Name,
                kind = ToSymbolKind(m.MemberType),
                _kind = m.MemberType.ToString().ToLowerInvariant()
            };

            var loc = m.Locations.FirstOrDefault();
            if (loc != null) {
                res.location = new Location {
                    uri = new Uri(loc.FilePath, UriKind.RelativeOrAbsolute),
                    range = new SourceSpan(
                        new SourceLocation(loc.StartLine, loc.StartColumn),
                        new SourceLocation(loc.EndLine ?? loc.StartLine, loc.EndColumn ?? loc.StartColumn)
                    )
                };
            }

            return res;
        }

        private SymbolKind ToSymbolKind(PythonMemberType memberType) {
            switch (memberType) {
                case PythonMemberType.Unknown: return SymbolKind.None;
                case PythonMemberType.Class: return SymbolKind.Class;
                case PythonMemberType.Instance: return SymbolKind.Object;
                case PythonMemberType.Delegate: return SymbolKind.Function;
                case PythonMemberType.DelegateInstance: return SymbolKind.Function;
                case PythonMemberType.Enum: return SymbolKind.Enum;
                case PythonMemberType.EnumInstance: return SymbolKind.EnumMember;
                case PythonMemberType.Function: return SymbolKind.Function;
                case PythonMemberType.Method: return SymbolKind.Method;
                case PythonMemberType.Module: return SymbolKind.Module;
                case PythonMemberType.Namespace: return SymbolKind.Namespace;
                case PythonMemberType.Constant: return SymbolKind.Constant;
                case PythonMemberType.Event: return SymbolKind.Event;
                case PythonMemberType.Field: return SymbolKind.Field;
                case PythonMemberType.Property: return SymbolKind.Property;
                case PythonMemberType.Multiple: return SymbolKind.Object;
                case PythonMemberType.Keyword: return SymbolKind.None;
                case PythonMemberType.CodeSnippet: return SymbolKind.None;
                case PythonMemberType.NamedArgument: return SymbolKind.None;
                default: return SymbolKind.None;
            }
        }

        private static IEnumerable<MemberResult> GetModuleVariables(
            IPythonProjectEntry entry,
            GetMemberOptions opts,
            string prefix
        ) {
            var analysis = entry?.Analysis;
            if (analysis == null) {
                yield break;
            }

            foreach (var m in analysis.GetAllAvailableMembers(SourceLocation.None, opts)) {
                if (m.Values.Any(v => v.DeclaringModule == entry)) {
                    if (string.IsNullOrEmpty(prefix) || m.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                        yield return m;
                    }
                }
            }
        }


    }
}
