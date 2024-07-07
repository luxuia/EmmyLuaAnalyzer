using System.Collections.Concurrent;
using System.IO;
using EmmyLua.CodeAnalysis.Compilation.Analyzer;
using EmmyLua.CodeAnalysis.Compilation.Search;
using EmmyLua.CodeAnalysis.Compilation.Semantic;
using EmmyLua.CodeAnalysis.Document;
using EmmyLua.CodeAnalysis.Syntax.Node.SyntaxNodes;
using EmmyLua.CodeAnalysis.Workspace;
using EmmyLua.CodeAnalysis.Workspace.Module;
using EmmyLua.Configuration;
using EmmyLua.LanguageServer.Server.Monitor;
using EmmyLua.LanguageServer.Server.Resource;
using EmmyLua.LanguageServer.Util;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using static System.Reflection.Metadata.BlobBuilder;


namespace EmmyLua.LanguageServer.Server;

public class ServerContext(ILanguageServerFacade server)
{
    public bool IsVscode { get; set; } = true;

    private HashSet<string> Extensions { get; } = new();

    private string MainWorkspacePath { get; set; } = string.Empty;

    private List<string> ExternalWorkspacePaths { get; } = new();

    private ReaderWriterLockSlim LockSlim { get; } = new();

    public LuaWorkspace LuaWorkspace { get; private set; } = LuaWorkspace.CleanCreate();

    public SettingManager SettingManager { get; } = new();

    public ILanguageServerFacade Server { get; } = server;

    private ProcessMonitor Monitor { get; } = new(server);

    public ResourceManager ResourceManager { get; } = new();

    private CancellationTokenSource? WorkspaceCancellationTokenSource { get; set; } = null;

    private bool ServerStrted = false;

    private class FileChange {
        public string uri;
        public string text;
        public CancellationToken token;
    }
    private Dictionary<string, FileChange> DelayFileChanges { get; } = new();

    private ConcurrentDictionary<LuaDocumentId, CancellationTokenSource> DocumentCancellationTokenSources { get; } =
        new();

    public async Task StartServerAsync(InitializeParams initializeParams, ILanguageServer configServer)
    {
        IsVscode = string.Equals(initializeParams.ClientInfo?.Name, "Visual Studio Code",
            StringComparison.CurrentCultureIgnoreCase);

        if (IsVscode)
        {
            MainWorkspacePath = initializeParams.RootPath;

            var config = await configServer.Configuration.GetConfiguration(new[]
            {
                new ConfigurationItem()
                {
                    Section = "files"
                },
            });
            
            foreach (var section in config.GetSection("files:associations").GetChildren())
            {
                if (section.Value == "lua")
                {
                    Extensions.Add(section.Key);
                }
            }
            
            if (config.GetSection("files:encoding").Value is {} encoding)
            {
                SettingManager.WorkspaceEncoding = encoding;
            }
        }

        StartServer(initializeParams);
    }

    private void StartServer(InitializeParams initializeParams)
    {
        LockSlim.EnterWriteLock();
        try
        {
            if (initializeParams.RootPath is { } rootPath)
            {
                MainWorkspacePath = rootPath;
                LuaWorkspace.Monitor = Monitor;
                SettingManager.SupportMultiEncoding();
                SettingManager.Watch(MainWorkspacePath);
                SettingManager.OnSettingChanged += OnConfigChanged;
                SettingManager.WorkspaceExtensions = Extensions;
                LuaWorkspace.Features = SettingManager.GetLuaFeatures();
                LuaWorkspace.InitStdLib();
                if (IsVscode && initializeParams.WorkspaceFolders is { } workspaceFolders)
                {
                    foreach (var workspaceFolder in workspaceFolders)
                    {
                        var path = workspaceFolder.Uri.ToUri().LocalPath;
                        if (path != MainWorkspacePath)
                        {
                            ExternalWorkspacePaths.Add(path);
                            LuaWorkspace.LoadWorkspace(path);
                        }
                    }
                }

                LuaWorkspace.LoadMainWorkspace(MainWorkspacePath);
                ResourceManager.Config = SettingManager.GetResourceConfig();
                WorkspaceCancellationTokenSource = new CancellationTokenSource();
                PushWorkspaceDiagnostics();
            }
            else
            {
                LuaWorkspace.InitStdLib();
            }

            ServerStrted = true;
        }
        finally
        {
            LockSlim.ExitWriteLock();
        }


        foreach (var v in DelayFileChanges.Values) {
            UpdateDocument(v.uri, v.text, v.token);
        }
        DelayFileChanges.Clear();
    }

    public void ReadyWrite(Action action)
    {
        LockSlim.EnterWriteLock();
        try
        {
            action();
        }
        finally
        {
            LockSlim.ExitWriteLock();
        }
    }

    public void ReadyRead(Action action)
    {
        LockSlim.EnterReadLock();
        try
        {
            action();
        }
        finally
        {
            LockSlim.ExitReadLock();
        }
    }

    public SemanticModel? GetSemanticModel(string uri)
    {
        return LuaWorkspace.Compilation.GetSemanticModel(uri);
    }

    public SemanticModel? GetSemanticModel(LuaDocumentId documentId)
    {
        return LuaWorkspace.Compilation.GetSemanticModel(documentId);
    }

    private void OnConfigChanged(SettingManager settingManager)
    {
        LockSlim.EnterWriteLock();
        try
        {
            var features = settingManager.GetLuaFeatures();
            UpdateFeatures(features);
            ResourceManager.Config = SettingManager.GetResourceConfig();
        }
        finally
        {
            LockSlim.ExitWriteLock();
        }
    }

    private void UpdateFeatures(LuaFeatures newFeatures)
    {
        var oldFeatures = LuaWorkspace.Features;
        var workspaceNeedReload = false;
        workspaceNeedReload |= !newFeatures.RequirePattern.SequenceEqual(oldFeatures.RequirePattern);
        workspaceNeedReload |= !newFeatures.ExcludeFolders.SequenceEqual(oldFeatures.ExcludeFolders);
        workspaceNeedReload |= !newFeatures.Extensions.SequenceEqual(oldFeatures.Extensions);
        workspaceNeedReload |= !newFeatures.WorkspaceRoots.SequenceEqual(oldFeatures.WorkspaceRoots);
        workspaceNeedReload |= !newFeatures.ThirdPartyRoots.SequenceEqual(oldFeatures.ThirdPartyRoots);
        if (workspaceNeedReload)
        {
            LuaWorkspace = LuaWorkspace.CleanCreate();
            LuaWorkspace.Monitor = Monitor;
            LuaWorkspace.Features = newFeatures;
            LuaWorkspace.InitStdLib();
            foreach (var workspacePath in ExternalWorkspacePaths)
            {
                LuaWorkspace.LoadWorkspace(workspacePath);
            }

            LuaWorkspace.LoadMainWorkspace(MainWorkspacePath);
            PushWorkspaceDiagnostics();
        }
        else // TODO check condition
        {
            LuaWorkspace.Features = newFeatures;
            PushWorkspaceDiagnostics();
        }
    }

    private void PushWorkspaceDiagnostics()
    {
        WorkspaceCancellationTokenSource?.Cancel();
        WorkspaceCancellationTokenSource = new CancellationTokenSource();
        _ = Task.Run(async () => { await PushWorkspaceDiagnosticsAsync(WorkspaceCancellationTokenSource.Token); },
            WorkspaceCancellationTokenSource.Token);
    }

    private async Task PushWorkspaceDiagnosticsAsync(CancellationToken cancellationToken)
    {
        Monitor.OnStartDiagnosticCheck();
        var documents = LuaWorkspace.AllDocuments.ToList();
        var diagnosticCount = documents.Count;
        var context = new ThreadLocal<SearchContext>(() =>
            new SearchContext(LuaWorkspace.Compilation, new SearchContextFeatures()));
        try
        {
            var tasks = new List<Task>();
            var currentCount = 0;
            foreach (var document in LuaWorkspace.AllDocuments)
            {
                tasks.Add(Task.Run(() =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var count = Interlocked.Increment(ref currentCount);
                    Monitor.OnDiagnosticChecking(count, diagnosticCount);
                    LockSlim.EnterReadLock();
                    try
                    {
                        // ReSharper disable once AccessToDisposedClosure
                        var diagnostics = LuaWorkspace.Compilation.GetDiagnostics(document.Id, context.Value!);
                        Server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
                        {
                            Diagnostics = Container.From(diagnostics.Select(it => it.ToLspDiagnostic(document))),
                            Uri = document.Uri,
                        });
                    }
                    finally
                    {
                        LockSlim.ExitReadLock();
                    }
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            context.Dispose();
        }

        Monitor.OnFinishDiagnosticCheck();
        GC.Collect();
    }


    string GetPathByModule(string module) {
        if (LuaWorkspace.ModuleManager.FindModule(module) != null) return null;

        var testPath = Path.Combine(MainWorkspacePath, module + ".lua");
        if (File.Exists(testPath)) {
            return testPath;
        }

        return null;
    }

    public void UpdateDocument(string uri, string text, CancellationToken cancellationToken)
    {
        if ( !ServerStrted)
        {
            DelayFileChanges[uri] = new FileChange() { uri = uri, text = text, token = cancellationToken };
            return;
        }

        LuaDocumentId documentId = LuaDocumentId.VirtualDocumentId;
        ReadyWrite(() =>
        {
            LuaWorkspace.UpdateDocumentByUri(uri, text);
            documentId = LuaWorkspace.GetDocumentIdByUri(uri) ?? LuaDocumentId.VirtualDocumentId;

            var SyntaxTree = LuaWorkspace.GetDocument(documentId).SyntaxTree;

            var luafeature = SettingManager.GetLuaFeatures();
            var blocks = SyntaxTree.SyntaxRoot.Descendants.OfType<LuaCallArgListSyntax>();

            var excludeFolders = luafeature.ExcludeFolders;

            foreach (var block in blocks) {
                if (block.Parent is LuaCallExprSyntax require && luafeature.RequireLikeFunction.Contains( require.Name)) {
                    var path = GetPathByModule(block.Text.ToString().Replace("\"", "").Replace("'", ""));

                    //没必要做检查，也不大
                    //var exclude = !excludeFolders.Any(filter => path.Contains(filter));
                    if (!string.IsNullOrEmpty(path) && LuaWorkspace.GetDocumentByPath(path) == null) {
                        var doc = LuaDocument.OpenDocument(path, LuaWorkspace.Features.Language);
                        LuaWorkspace.AddDocument(doc);
                    }
                }
            }
        });

        if (documentId != LuaDocumentId.VirtualDocumentId)
        {
            PushDocumentDiagnostics(documentId);
        }
    }

    public void UpdateManyDocuments(List<FileEvent> fileEvents, CancellationToken cancellationToken)
    {
        var documentIds = new List<LuaDocumentId>();
        ReadyWrite(() =>
        {
            LuaWorkspace.Compilation.BulkUpdate(() =>
            {
                foreach (var fileEvent in fileEvents)
                {
                    switch (fileEvent)
                    {
                        case { Type: FileChangeType.Created }:
                        case { Type: FileChangeType.Changed }:
                        {
                            var uri = fileEvent.Uri.ToUri().AbsoluteUri;
                            var fileText = File.ReadAllText(fileEvent.Uri.GetFileSystemPath());
                            LuaWorkspace.UpdateDocumentByUri(uri, fileText);
                            var documentId = LuaWorkspace.GetDocumentIdByUri(uri);
                            if (documentId.HasValue)
                            {
                                documentIds.Add(documentId.Value);
                            }

                            break;
                        }
                        case { Type: FileChangeType.Deleted }:
                        {
                            LuaWorkspace.RemoveDocumentByUri(fileEvent.Uri.ToUri().AbsoluteUri);
                            break;
                        }
                    }
                }
            });
        });

        foreach (var documentId in documentIds)
        {
            PushDocumentDiagnostics(documentId);
        }
    }

    public void RemoveDocument(string uri)
    {
        ReadyWrite(() => { LuaWorkspace.RemoveDocumentByUri(uri); });
    }

    private void PushDocumentDiagnostics(LuaDocumentId documentId)
    {
        LockSlim.EnterReadLock();
        try
        {
            var document = LuaWorkspace.GetDocument(documentId);
            if (document is null)
            {
                return;
            }

            var context = new SearchContext(LuaWorkspace.Compilation, new SearchContextFeatures());
            var diagnostics = LuaWorkspace.Compilation.GetDiagnostics(document.Id, context);
            Server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams()
            {
                Diagnostics = Container.From(diagnostics.Select(it => it.ToLspDiagnostic(document))),
                Uri = document.Uri,
            });
        }
        finally
        {
            LockSlim.ExitReadLock();
        }
    }
}