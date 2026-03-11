using System;
using UnityEngine;

public class BLASBuilder
{
    public BuildTriangle[] BuildTriangles { get; private set; }
    public BvhNode[] Nodes { get; private set; }
    public int[] PrimitiveRefs { get; private set; }
    public int RootIndex { get; private set; }

    public void Build(Mesh mesh, BvhBuildSettings settings)
    {
        if (mesh == null)
            throw new ArgumentNullException(nameof(mesh));

        BuildTriangles = MeshPreprocessor.BuildTrianglesFromMesh(mesh);

        PrimitiveRefs = new int[BuildTriangles.Length];
        for (int i = 0; i < PrimitiveRefs.Length; i++)
            PrimitiveRefs[i] = i;

        var builder = new BvhBuilder<int>(
            PrimitiveRefs,
            new TriangleAdapter(BuildTriangles),
            settings
        );

        RootIndex = builder.Build();

        Nodes = new BvhNode[builder.Nodes.Count];
        for (int i = 0; i < Nodes.Length; i++)
            Nodes[i] = builder.Nodes[i];

        PrimitiveRefs = builder.PrimitiveRefs;
    }
}