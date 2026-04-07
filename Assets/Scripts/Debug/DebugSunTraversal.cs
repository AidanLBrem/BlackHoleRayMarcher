using UnityEngine;

public class DebugSunTraversal : MonoBehaviour
{
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public float degreesPerSecondX;
    public float degreesPerSecondY;
    public float degreesPerSecondZ;
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        transform.Rotate(degreesPerSecondX * Time.deltaTime, degreesPerSecondY * Time.deltaTime, degreesPerSecondZ * Time.deltaTime);
    }
}
