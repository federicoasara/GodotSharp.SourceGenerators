﻿using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace GodotSharp.SourceGenerators.SceneTreeExtensions
{
    internal static class SceneTreeScraper
    {
        private enum Phase
        {
            NodeScan,
            PropertyScan,
            ResourceScan,
            EditableScan,
        }

        private const string NodeRegexStr = @"^\[node name=""(?<Name>.*?)""( type=""(?<Type>.*?)"")?( parent=""(?<Parent>.*?)"")?( index=""(?<Index>.*?)"")?( instance=ExtResource\( (?<Id>\d*))?( instance_placeholder=""res:/(?<PlaceholderPath>.*)"")?";
        private const string ScriptRegexStr = @"^script = ExtResource\( (?<Id>\d*)";
        private const string UniqueNameRegexStr = @"^unique_name_in_owner = true";
        private const string ResourceRegexStr = @"^\[ext_resource path=""res:/(?<Path>.*)"" type=""(?<Type>.*)"" id=(?<Id>\d*)";
        private const string EditableRegexStr = @"^\[editable path=""(?<Path>.*)""";

        private static readonly Regex NodeRegex = new(NodeRegexStr, RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private static readonly Regex ScriptRegex = new(ScriptRegexStr, RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private static readonly Regex UniqueNameRegex = new(UniqueNameRegexStr, RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private static readonly Regex ResourceRegex = new(ResourceRegexStr, RegexOptions.Compiled | RegexOptions.ExplicitCapture);
        private static readonly Regex EditableRegex = new(EditableRegexStr, RegexOptions.Compiled | RegexOptions.ExplicitCapture);

        private static readonly Dictionary<string, Tree<SceneTreeNode>> sceneTreeCache = new();
        private static string _resPath = null;

        public static (Tree<SceneTreeNode> SceneTree, List<SceneTreeNode> UniqueNodes) GetNodes(Compilation compilation, string tscnFile, bool traverseInstancedScenes)
        {
            Log.Debug();
            tscnFile = tscnFile.Replace("\\", "/");
            Log.Debug($"Scraping {tscnFile} [CacheCount: {sceneTreeCache.Count}]");

            var phase = Phase.ResourceScan;
            var resources = new Dictionary<string, string>();

            var first = true;
            SceneTreeNode curNode = null;
            Tree<SceneTreeNode> sceneTree = null;
            List<SceneTreeNode> uniqueNodes = new();
            Dictionary<string, TreeNode<SceneTreeNode>> nodeLookup = new();

            foreach (var line in File.ReadLines(tscnFile).Skip(2))
            {
                Log.Debug($"Line: {line}");

                if (first)
                {
                    first = false;
                    if (line.StartsWith("[node"))
                        phase = Phase.NodeScan;
                }
                else if (line is "")
                {
                    phase = Phase.NodeScan;
                    continue;
                }

                Match match = null;

                switch (phase)
                {
                    case Phase.NodeScan: NodeScan(); break;
                    case Phase.PropertyScan: PropertyScan(); break;
                    case Phase.ResourceScan: ResourceScan(); break;
                    case Phase.EditableScan: EditableScan(); break;
                }

                void NodeScan()
                {
                    match = NodeRegex.Match(line);
                    if (match.Success)
                        NodeScan();
                    else if (EditableScan())
                        phase = Phase.EditableScan;

                    void NodeScan()
                    {
                        Log.Debug($"Matched Node: {NodeRegex.GetGroupsAsStr(match)}");
                        var nodeName = match.Groups["Name"].Value;
                        var nodeType = match.Groups["Type"].Value;
                        var parentPath = match.Groups["Parent"].Value;
                        var resourceId = match.Groups["Id"].Value;
                        var placeholderPath = match.Groups["PlaceholderPath"].Value;

                        if (placeholderPath is not "")
                        {
                            Debug.Assert(nodeType is "");
                            nodeType = "InstancePlaceholder";
                        }

                        var nodePath = GetNodePath();
                        var safeNodeName = nodeName.Replace("-", "_");

                        AddNode(safeNodeName, nodePath);

                        phase = Phase.PropertyScan;

                        void AddNode(string nodeName, string nodePath)
                        {
                            if (IsRootNode())
                            {
                                if (HasResource()) // Inherited Scene
                                {
                                    var resource = GetResource();
                                    Log.Debug($" - InheritedScene: {resource}");
                                    if (!sceneTreeCache.TryGetValue(resource, out var parentScene))
                                    {
                                        parentScene = GetNodes(compilation, resource, traverseInstancedScenes).SceneTree;
                                        Log.Debug();
                                        Log.Debug($"<<< {tscnFile}");
                                    }

                                    parentScene.Traverse(x =>
                                    {
                                        if (x.IsRoot)
                                            AddNode(curNode = new SceneTreeNode(nodeName, x.Value.Type, x.Value.Path));
                                        else
                                            AddNode(new SceneTreeNode(x.Value.Name, x.Value.Type, x.Value.Path), x.Parent.IsRoot ? "." : x.Parent.Value.Path);
                                    });
                                }
                                else // Root Node (normal)
                                {
                                    AddNode(curNode = new SceneTreeNode(nodeName, $"Godot.{nodeType}", nodePath), parentPath);
                                    Log.Debug($" - RootNode: {curNode}");
                                }
                            }
                            else if (HasResource()) // Instanced Scene
                            {
                                var resource = GetResource();
                                Log.Debug($" - InstancedScene: {resource}");
                                if (!sceneTreeCache.TryGetValue(resource, out var instancedScene))
                                {
                                    instancedScene = GetNodes(compilation, resource, traverseInstancedScenes).SceneTree;
                                    Log.Debug();
                                    Log.Debug($"<<< {tscnFile}");
                                }

                                instancedScene.Traverse(x =>
                                {
                                    if (x.IsRoot)
                                        AddNode(curNode = new SceneTreeNode(nodeName, x.Value.Type, nodePath), parentPath);
                                    else
                                        AddNode(new SceneTreeNode(x.Value.Name, x.Value.Type, $"{nodePath}/{x.Value.Path}", traverseInstancedScenes), x.Parent.IsRoot ? nodePath : $"{nodePath}/{x.Parent.Value.Path}");
                                });
                            }
                            else if (nodeType is "") // Inherited/Instanced Node (already added, potentially modified)
                            {
                                curNode = nodeLookup[nodePath].Value;
                                Log.Debug($" - Child Node (inherited/instanced): {curNode}");
                            }
                            else // Node (normal)
                            {
                                AddNode(curNode = new SceneTreeNode(nodeName, $"Godot.{nodeType}", nodePath), parentPath);
                                Log.Debug($" - Node: {curNode}");
                            }

                            void AddNode(SceneTreeNode node, string parentPath = null)
                            {
                                if (sceneTree is null) // Root
                                    nodeLookup.Add(".", sceneTree = new(node));
                                else
                                    nodeLookup.Add(node.Path, nodeLookup[parentPath].Add(node));
                            }
                        }

                        bool IsRootNode()
                            => parentPath is "";

                        bool IsChildNode()
                            => parentPath is ".";

                        bool HasResource()
                            => resourceId is not "";

                        string GetResource()
                        {
                            var resource = resources[resourceId];
                            return GetResPath(resource) + resource;

                            string GetResPath(string resource)
                            {
                                return _resPath is null || !tscnFile.StartsWith(_resPath)
                                    ? _resPath = TryGetFromSceneCache() ?? TryGetFromFileSystem() : _resPath;

                                string TryGetFromSceneCache()
                                    => sceneTreeCache.Keys.FirstOrDefault(x => x.EndsWith(resource))?[..^resource.Length];

                                string TryGetFromFileSystem()
                                {
                                    const string GodotProjectFile = "project.godot";
                                    var tscnFolder = Path.GetDirectoryName(tscnFile);

                                    while (tscnFolder is not null)
                                    {
                                        if (File.Exists($"{tscnFolder}/{GodotProjectFile}"))
                                            return tscnFolder;

                                        tscnFolder = Path.GetDirectoryName(tscnFolder);
                                    }

                                    throw new Exception($"Could not find {GodotProjectFile} in path {Path.GetDirectoryName(tscnFile)}");
                                }
                            }
                        }

                        string GetNodePath()
                            => IsRootNode() ? "" : IsChildNode() ? nodeName : $"{parentPath}/{nodeName}";
                    }
                }

                void PropertyScan()
                {
                    if (ScriptScan()) return;
                    if (UniqueNameScan()) return;

                    bool ScriptScan()
                    {
                        match = ScriptRegex.Match(line);
                        if (!match.Success) return false;

                        Log.Debug($"Matched Script: {ScriptRegex.GetGroupsAsStr(match)}");
                        var resource = resources[match.Groups["Id"].Value];
                        var name = Path.GetFileNameWithoutExtension(resource);
                        curNode.Type = compilation.GetFullName(name, resource);
                        Log.Debug($" - {curNode}");
                        return true;
                    }

                    bool UniqueNameScan()
                    {
                        match = UniqueNameRegex.Match(line);
                        if (!match.Success) return false;

                        Log.Debug($"Matched UniqueName:");
                        Log.Debug($" - {curNode}");
                        uniqueNodes.Add(curNode);
                        return true;
                    }
                }

                void ResourceScan()
                {
                    match = ResourceRegex.Match(line);
                    if (match.Success && match.Groups["Type"].Value is "Script" or "PackedScene")
                    {
                        Log.Debug($"Matched Resource: {ResourceRegex.GetGroupsAsStr(match)}");
                        resources.Add(match.Groups["Id"].Value, match.Groups["Path"].Value);
                    }
                }

                bool EditableScan()
                {
                    match = EditableRegex.Match(line);
                    if (match.Success)
                    {
                        Log.Debug($"Matched Editable: {EditableRegex.GetGroupsAsStr(match)}");
                        var node = nodeLookup[match.Groups["Path"].Value];
                        node.Value.Visible = true;
                        Log.Debug($" - {node.Value}");
                        return true;
                    }

                    return false;
                }
            }

            sceneTreeCache[tscnFile] = sceneTree;
            return (sceneTree, uniqueNodes);
        }
    }
}
