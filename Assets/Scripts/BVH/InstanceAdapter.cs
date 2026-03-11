using UnityEngine;

public class InstanceAdapter : IBvhAdapter<int>
{
    private readonly BvhInstance[] instances;

    public InstanceAdapter(BvhInstance[] instances)
    {
        this.instances = instances;
    }

    public Bounds GetBounds(int item) => instances[item].worldBounds;
    public Vector3 GetCentroid(int item) => instances[item].worldBounds.center;
}