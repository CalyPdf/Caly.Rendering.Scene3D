namespace Caly.Rendering.Scene3D.U3D.Internal;

internal sealed partial class U3DDocument
{
    public void BuildScene(SceneBuilder builder)
    {
        builder.UnitScale = _header.UnitsScale;

        if (_header.CharacterEncoding != 106)
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "character-encoding", $"U3D character encoding {_header.CharacterEncoding} was read as UTF-8.");
        }

        for (int i = 0; i < _duplicateNodes; i++)
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "duplicate-node", "A U3D node name is declared more than once; the last declaration is used.");
        }

        for (int i = 0; i < _nonFiniteTransforms; i++)
        {
            builder.Report(SceneDiagnosticSeverity.Warning, "non-finite-transform", "A U3D node transform holds a NaN or infinite value and was replaced by the identity.");
        }

        ReportSkippedBlocks(builder);
        ReportStrayBaseMeshes(builder);
        BuildTextures(builder);
        BuildMaterials(builder);
        BuildNodes(builder);
        BuildAnimations(builder);
    }

    /// <summary>
    /// Emits the node tree. A node declared under several parents is emitted once per path from the world,
    /// so the result is a tree with parents before children.
    /// </summary>
    private void BuildNodes(SceneBuilder builder)
    {
        RejectCycles();

        // Children of each node, in declaration order. The world is the empty name.
        var children = new Dictionary<string, List<(NodeDeclaration Node, int ParentSlot)>>(StringComparer.Ordinal);
        List<NodeDeclaration>? unattached = null;
        List<(NodeDeclaration Node, int ParentSlot)>? underMissing = null;
        foreach (string nodeName in _nodeOrder)
        {
            NodeDeclaration node = _nodes[nodeName];
            if (node.Parents.Length == 0)
            {
                (unattached ??= []).Add(node);
                continue;
            }

            for (int slot = 0; slot < node.Parents.Length; slot++)
            {
                string parent = node.Parents[slot].Name;
                if (parent.Length != 0 && !_nodes.ContainsKey(parent))
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "node-parent-missing", $"U3D node '{node.Name}' names a parent '{parent}' that does not exist.");
                    (underMissing ??= []).Add((node, slot));
                    continue;
                }

                if (!children.TryGetValue(parent, out List<(NodeDeclaration, int)>? list))
                {
                    children[parent] = list = [];
                }

                list.Add((node, slot));
            }
        }

        if (!children.TryGetValue("", out List<(NodeDeclaration Node, int ParentSlot)>? roots))
        {
            if (unattached is null && underMissing is null)
            {
                return;
            }

            // Nothing hangs on the world: read to the letter there is no scene. There are writers that
            // give no node a parent (circuit board exports), and writers that hang their one root on a
            // node they never declare, and mean them to be shown.
            builder.Report(SceneDiagnosticSeverity.Warning, "node-roots-assumed", "No U3D node hangs on the world; the nodes without a parent, or under a parent that does not exist, are taken as its children.");
            roots = underMissing ?? [];
            foreach (NodeDeclaration node in unattached ?? [])
            {
                roots.Add((node, -1));
            }
        }
        else if (unattached is not null)
        {
            foreach (NodeDeclaration node in unattached)
            {
                builder.Report(SceneDiagnosticSeverity.Info, "node-not-attached", $"U3D node '{node.Name}' has no parent and is not part of the scene.");
            }
        }

        // Depth-first, without recursion: the depth is under the file's control.
        var stack = new Stack<(NodeDeclaration Node, int ParentSlot, int SceneParent, int Depth)>();
        for (int i = roots.Count - 1; i >= 0; i--)
        {
            stack.Push((roots[i].Node, roots[i].ParentSlot, -1, 1));
        }

        while (stack.Count > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            (NodeDeclaration node, int slot, int sceneParent, int depth) = stack.Pop();
            if (depth > _limits.MaxNestingDepth)
            {
                throw new Scene3DException(Scene3DError.LimitExceeded, $"The U3D node tree is deeper than {_limits.MaxNestingDepth}.");
            }

            int sceneIndex = builder.AddNode(CreateSceneNode(builder, node, slot, sceneParent));

            if (children.TryGetValue(node.Name, out List<(NodeDeclaration Node, int ParentSlot)>? list))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    stack.Push((list[i].Node, list[i].ParentSlot, sceneIndex, depth + 1));
                }
            }
        }
    }

    private SceneNode CreateSceneNode(SceneBuilder builder, NodeDeclaration node, int parentSlot, int sceneParent)
    {
        var sceneNode = new SceneNode
        {
            Parent = sceneParent,
            Local = parentSlot < 0 ? System.Numerics.Matrix4x4.Identity : node.Parents[parentSlot].Matrix,
            Kind = node.Kind,
            Mesh = -1,
            Light = -1,
            Camera = -1,
            Name = builder.AddName(node.Name),
            Visible = node.Kind != SceneNodeKind.Model || node.Visibility != 0
        };

        switch (node.Kind)
        {
            case SceneNodeKind.Model:
                sceneNode.Mesh = GetMesh(builder, node);
                (sceneNode.FirstBinding, sceneNode.BindingCount) = GetBindings(builder, node);
                break;

            case SceneNodeKind.Light:
                if (_lightResources.TryGetValue(node.Resource, out SceneLight light))
                {
                    light.Name = builder.AddName(node.Resource);
                    sceneNode.Light = builder.AddLight(light);
                }
                else
                {
                    builder.Report(SceneDiagnosticSeverity.Warning, "light-resource-missing", $"U3D light node '{node.Name}' names a light resource '{node.Resource}' that does not exist.");
                }

                break;

            case SceneNodeKind.Camera:
                SceneCamera camera = node.Camera;
                camera.Name = sceneNode.Name;
                sceneNode.Camera = builder.AddCamera(camera);
                break;
        }

        return sceneNode;
    }

    /// <summary>
    /// A node that is its own ancestor is illegal (ECMA-363 9.5.1.2) and would make the tree infinite.
    /// Checked over the whole declared graph, reachable from the world or not.
    /// </summary>
    private void RejectCycles()
    {
        // 0 = unvisited, 1 = on the current path, 2 = finished
        var state = new Dictionary<NodeDeclaration, byte>(_nodeOrder.Count);
        var path = new Stack<(NodeDeclaration Node, int NextParent)>();

        foreach (string startName in _nodeOrder)
        {
            NodeDeclaration start = _nodes[startName];
            if (state.ContainsKey(start))
            {
                continue;
            }

            state[start] = 1;
            path.Push((start, 0));
            while (path.Count > 0)
            {
                (NodeDeclaration node, int next) = path.Pop();
                if (next >= node.Parents.Length)
                {
                    state[node] = 2;
                    continue;
                }

                path.Push((node, next + 1));
                if (!_nodes.TryGetValue(node.Parents[next].Name, out NodeDeclaration? parent))
                {
                    continue;
                }

                if (state.TryGetValue(parent, out byte parentState))
                {
                    if (parentState == 1)
                    {
                        throw Invalid($"U3D node '{parent.Name}' is its own ancestor.");
                    }

                    continue;
                }

                state[parent] = 1;
                path.Push((parent, 0));
            }
        }
    }
}
