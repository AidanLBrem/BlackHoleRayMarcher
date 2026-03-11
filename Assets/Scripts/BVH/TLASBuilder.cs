using UnityEngine;

public class TLASBuilder
{
    public BvhInstance[] Instances { get; private set; }
    public BvhNode[] Nodes { get; private set; }
    public int[] PrimitiveRefs { get; private set; }
    public int RootIndex { get; private set; }

    public void Build(BvhInstance[] instances, BvhBuildSettings settings)
    {
        Instances = instances ?? new BvhInstance[0];

        PrimitiveRefs = new int[Instances.Length];
        for (int i = 0; i < PrimitiveRefs.Length; i++)
            PrimitiveRefs[i] = i;

        var builder = new BvhBuilder<int>(
            PrimitiveRefs,
            new InstanceAdapter(Instances),
            settings
        );

        RootIndex = builder.Build();

        Nodes = new BvhNode[builder.Nodes.Count];
        for (int i = 0; i < Nodes.Length; i++)
            Nodes[i] = builder.Nodes[i];

        PrimitiveRefs = builder.PrimitiveRefs;
    }
}