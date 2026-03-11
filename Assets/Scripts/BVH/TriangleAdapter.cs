using UnityEngine;

public class TriangleAdapter : IBvhAdapter<int>
{
    private readonly BuildTriangle[] triangles;

    public TriangleAdapter(BuildTriangle[] triangles)
    {
        this.triangles = triangles;
    }

    public Bounds GetBounds(int item) => triangles[item].bounds;
    public Vector3 GetCentroid(int item) => triangles[item].centroid;
}