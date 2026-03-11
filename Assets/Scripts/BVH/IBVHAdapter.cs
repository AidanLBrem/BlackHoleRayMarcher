using UnityEngine;

public interface IBvhAdapter<TRef>
{
    Bounds GetBounds(TRef item);
    Vector3 GetCentroid(TRef item);
}