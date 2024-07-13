using EmmyLua.LanguageServer.Completion.CompleteProvider;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Reflection.Metadata;

using EmmyLua.CodeAnalysis.Workspace;
using System.Collections.Generic;
namespace EmmyLua.LanguageServer.Completion;

public class CompletionBuilder
{
    private List<ICompleteProviderBase> Providers { get; } = [
        new RequireProvider(),
        new ResourcePathProvider(),
        new AliasAndEnumProvider(),
        new TableFieldProvider(),
        new LocalEnvProvider(),
        new GlobalProvider(),
        new KeywordsProvider(),
        new MemberProvider(),
        new ModuleProvider(),
        new DocProvider(),
        new SelfMemberProvider(),
        new PostfixProvider()
    ];
    
    public List<CompletionItem> Build(CompleteContext completeContext)
    {
        try
        {
            List<CompletionItem> ret;
            var document = completeContext.ServerContext.LuaWorkspace.GetDocument(completeContext.TriggerToken.DocumentId);
            using (var log = new Logger.Log($"Completion {document.Path} - {completeContext.TriggerToken.RepresentText}")) {

                foreach (var provider in Providers) {
                    provider.AddCompletion(completeContext);
                    if (!completeContext.Continue) {
                        break;
                    }
                }
                ret = completeContext.CompletionItems.ToList();
                log.extra = $" Count {ret.Count}";
            }
            return ret;
        }
        catch (OperationCanceledException)
        {
            return new();
        }
    }
}