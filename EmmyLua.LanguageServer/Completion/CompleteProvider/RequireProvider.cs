using EmmyLua.CodeAnalysis.Syntax.Node.SyntaxNodes;
using EmmyLua.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using static EmmyLua.CodeAnalysis.Workspace.Module.ModuleManager;

namespace EmmyLua.LanguageServer.Completion.CompleteProvider;

public class RequireProvider : ICompleteProviderBase
{
    public void AddCompletion(CompleteContext context)
    {
        var trigger = context.TriggerToken;
        if (trigger is LuaStringToken modulePathToken
            && trigger.Parent?.Parent?.Parent is LuaCallExprSyntax { Name: { } funcName }
            && context.SemanticModel.Compilation.Workspace.Features.RequireLikeFunction.Contains(funcName))
        {
            var moduleInfos =
                context.SemanticModel.Compilation.Workspace.ModuleManager.GetCurrentModuleNames(modulePathToken.Value);
            
            var modulePath = modulePathToken.Value;
            var parts = modulePath.Split('.');
            var moduleBase = string.Empty;
            if (parts.Length > 1)
            {
                moduleBase = string.Join('.', parts[..^1]);
            }

            var addedMap = new HashSet<string>();
            foreach (var moduleInfo in moduleInfos)
            {
                var filterText = moduleInfo.Name;
                if (moduleBase.Length != 0)
                {
                    filterText = $"{moduleBase}.{filterText}";
                }
                addedMap.Add(filterText);
                context.Add(new CompletionItem
                {
                    Label = moduleInfo.Name,
                    Kind = moduleInfo.IsFile ? CompletionItemKind.File : CompletionItemKind.Folder,
                    Detail = moduleInfo.Uri,
                    FilterText = filterText,
                    InsertText = filterText,
                    Data = moduleInfo.DocumentId?.Id.ToString()
                });
            }

            var Workspace = context.SemanticModel.Compilation.Workspace;
            var realWorkSpace = Workspace.ModuleManager.GetWorkspace(context.SemanticModel.Document);
            if (realWorkSpace == null) {
                realWorkSpace = Workspace.MainWorkspace;
            }
            var rootpath = Path.Combine(realWorkSpace, string.Join(Path.DirectorySeparatorChar, parts[..^1]));
            if (Directory.Exists(rootpath)) {
                // 根据require延迟加载的情况, 文件夹下面的没有加载进module, 需要这里处理

                var lastpart = parts[parts.Length - 1];
                var files = Directory.GetFiles(rootpath, "*.lua");
                foreach (var file in files) {

                    var relatepath = file.Replace(realWorkSpace, "").Trim(Path.DirectorySeparatorChar);
                    var name = relatepath.Replace(Path.DirectorySeparatorChar, '.').Replace(".lua", "");

                    var suffix = name.Replace(moduleBase + ".", "");
                    if (suffix.StartsWith(lastpart)) {

                        if (!addedMap.Contains(name)) {
                            addedMap.Add(name);
                            context.Add(new CompletionItem {
                                Label = suffix,
                                Kind = CompletionItemKind.File,
                                Detail = relatepath,
                                FilterText = name,
                                InsertText = name
                            });
                        }
                    }
                }

                var dirs = Directory.GetDirectories(rootpath);
                foreach (var file in dirs) {

                    var relatepath = file.Replace(realWorkSpace, "").Trim(Path.DirectorySeparatorChar);
                    var name = relatepath.Replace(Path.DirectorySeparatorChar, '.');

                    var suffix = name.Replace(moduleBase + ".", "");
                    if (suffix.StartsWith(lastpart)) {

                        if (!addedMap.Contains(name)) {
                            addedMap.Add(name);
                            context.Add(new CompletionItem {
                                Label = suffix,
                                Kind = CompletionItemKind.Folder,
                                Detail = relatepath,
                                FilterText = name,
                                InsertText = name
                            });
                        }
                    }
                }
            }

            context.StopHere();
        }
    }
}