using UnityEngine;

public class CameraTestScript : MonoBehaviour
{
    public int numPoints = 10;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void OnDrawGizmos() {
        CameraRayTest();
    }

    // Update is called once per frame
    void Update()
    {
                CameraRayTest();
    }

    void CameraRayTest() {
        Camera cam = Camera.main;
        Transform camT = cam.transform;

        float planeHeight = cam.nearClipPlane * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth = planeHeight * cam.aspect;

        Vector3 bottomLeftLocal = new Vector3(-planeWidth * 0.5f, -planeHeight * 0.5f, cam.nearClipPlane);

        for (int x = 0; x < numPoints; x++) {
            for (int y = 0; y < numPoints; y++) {
                float tx = x / (numPoints - 1f);
                float ty = y / (numPoints - 1f);

                Vector3 pointLocal = bottomLeftLocal + new Vector3(tx * planeWidth, ty * planeHeight, 0);

                Vector3 point = camT.position + camT.right * pointLocal.x + camT.up * pointLocal.y + camT.forward * pointLocal.z;
                Vector3 dir = (point - camT.position);
                DrawArrow(camT.position, dir, Color.blue);  
                DrawPoint(point);
            }
        }
    }

    void DrawPoint(Vector3 point) {
        Gizmos.color = Color.white;
        Gizmos.DrawSphere(point, 0.01f);
    }

	public static void DrawArrow(Vector3 pos, Vector3 direction, Color color, float arrowHeadLength = 0.025f, float arrowHeadAngle = 20.0f)
	{
        Gizmos.color = color;
		Gizmos.DrawRay(pos, direction);
		
		Vector3 right = Quaternion.LookRotation(direction) * Quaternion.Euler(0,180+arrowHeadAngle,0) * new Vector3(0,0,1);
		Vector3 left = Quaternion.LookRotation(direction) * Quaternion.Euler(0,180-arrowHeadAngle,0) * new Vector3(0,0,1);
		Gizmos.DrawRay(pos + direction, right * arrowHeadLength);
		Gizmos.DrawRay(pos + direction, left * arrowHeadLength);
	}
}
