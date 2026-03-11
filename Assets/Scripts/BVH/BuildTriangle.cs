using System;
using UnityEngine;

[Serializable]
public struct BuildTriangle
{
    public Vector3 posA;
    public Vector3 posB;
    public Vector3 posC;

    public Vector3 nA;
    public Vector3 nB;
    public Vector3 nC;

    public Bounds bounds;
    public Vector3 centroid;

    public static BuildTriangle Create(
        Vector3 posA, Vector3 posB, Vector3 posC,
        Vector3 nA, Vector3 nB, Vector3 nC)
    {
        Vector3 centroid = (posA + posB + posC) / 3f;
        Bounds bounds = new Bounds(centroid, Vector3.zero);
        bounds.Encapsulate(posA);
        bounds.Encapsulate(posB);
        bounds.Encapsulate(posC);

        return new BuildTriangle
        {
            posA = posA,
            posB = posB,
            posC = posC,
            nA = nA,
            nB = nB,
            nC = nC,
            bounds = bounds,
            centroid = centroid
        };
    }
}