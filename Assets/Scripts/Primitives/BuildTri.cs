using UnityEngine;
[System.Serializable]
public struct buildTri {
    public Vector3 posA;
    public Vector3 posB;
    public Vector3 posC;

    public Vector3 n1;
    public Vector3 n2;
    public Vector3 n3;
    public Bounds bounds;
    public Vector3 centroid;
    public int triangleIndex;

}