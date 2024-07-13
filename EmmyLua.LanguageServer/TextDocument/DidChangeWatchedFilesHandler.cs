using EmmyLua.LanguageServer.Server;
using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Workspace;
using Serilog;
using System.IO;

namespace EmmyLua.LanguageServer.TextDocument;

// ReSharper disable once ClassNeverInstantiated.Global
public class DidChangeWatchedFilesHandler(ServerContext context)
    : IDidChangeWatchedFilesHandler
{
    public DidChangeWatchedFilesRegistrationOptions GetRegistrationOptions() => new();

    public async Task<Unit> Handle(DidChangeWatchedFilesParams request, CancellationToken cancellationToken)
    {
        //DELAY_REQUIRE
        { return Unit.Value; }


        var changes = request.Changes.ToList();
        var list = new List<FileEvent>();

        var filters = context.LuaWorkspace.Features.ExcludeFolders;

        foreach (var fileEvent in changes) {
            if (fileEvent.Type == FileChangeType.Created || fileEvent.Type == FileChangeType.Changed) {
                var uri = fileEvent.Uri.ToUri().AbsoluteUri;
                if (!filters.Any(filter => uri.Contains(filter))) {
                    list.Add(fileEvent);
                }
            }
        }

        if (list.Count == 1)
        {
            return await UpdateOneFileEventAsync(list[0], cancellationToken);
        }
        else
        {
            return await UpdateManyFileEventAsync(list, cancellationToken);
        }
    }

    private Task<Unit> UpdateOneFileEventAsync(FileEvent fileEvent, CancellationToken cancellationToken)
    {
        switch (fileEvent.Type)
        {
            case FileChangeType.Created:
            case FileChangeType.Changed:
            {
                try
                {
                    var fileText = context.LuaWorkspace.ReadFile(fileEvent.Uri.ToUri().LocalPath);
                    var uri = fileEvent.Uri.ToUri().AbsoluteUri;
                    context.UpdateDocument(uri, fileText, cancellationToken);
                }
                catch (Exception e)
                {
                    Log.Logger.Error(e.Message);
                }

                break;
            }
            case FileChangeType.Deleted:
            {
                var uri = fileEvent.Uri.ToUri().AbsoluteUri;
                context.RemoveDocument(uri);
                break;
            }
        }

        return Unit.Task;
    }

    private Task<Unit> UpdateManyFileEventAsync(List<FileEvent> fileEvents,
        CancellationToken cancellationToken)
    {
        context.UpdateManyDocuments(fileEvents, cancellationToken);
        return Unit.Task;
    }

    public DidChangeWatchedFilesRegistrationOptions GetRegistrationOptions(DidChangeWatchedFilesCapability capability,
        ClientCapabilities clientCapabilities) => new();
}