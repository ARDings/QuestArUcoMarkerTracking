using UnityEngine;

public class DynamicDroneBuilder : MonoBehaviour
{
    public float voxelSize = 0.1f;
    public Material voxelMaterial;
    public AudioClip destroySound;
    
    private Color centerColor = Color.red;
    private Color middleRingColor = Color.yellow;
    private Color outerRingColor = Color.green;
    
    private float spacing = 0.05f; // Abstand zwischen den Ringen

    public GameObject BuildDrone()
    {
        GameObject drone = new GameObject("TargetDrone");
        DroneAI ai = drone.AddComponent<DroneAI>();
        ai.destroySound = destroySound;
        ai.userHead = Camera.main.transform;
        
        // Äußerer Ring (grün)
        CreateRing(drone.transform, 16, 0.5f, outerRingColor);
        
        // Mittlerer Ring (gelb)
        CreateRing(drone.transform, 8, 0.3f, middleRingColor);
        
        // Zentrum (rot)
        CreateRing(drone.transform, 4, 0.15f, centerColor);
        
        // Füge einen Collider für die Schwerterkennnung hinzu
        SphereCollider collider = drone.AddComponent<SphereCollider>();
        collider.radius = 0.5f;
        collider.isTrigger = true;

        return drone;
    }
    
    private void CreateRing(Transform parent, int voxelCount, float radius, Color color)
    {
        float angleStep = 360f / voxelCount;
        
        for (int i = 0; i < voxelCount; i++)
        {
            float angle = i * angleStep;
            float rad = angle * Mathf.Deg2Rad;
            
            Vector3 position = new Vector3(
                Mathf.Cos(rad) * radius,
                0,
                Mathf.Sin(rad) * radius
            );
            
            CreateVoxel(parent, position, color);
            
            // Wenn es das Zentrum ist, erstelle nur einen Voxel
            if (voxelCount == 1) break;
        }
    }
    
    private void CreateVoxel(Transform parent, Vector3 localPosition, Color color)
    {
        GameObject voxel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        voxel.transform.SetParent(parent);
        voxel.transform.localPosition = localPosition;
        voxel.transform.localScale = Vector3.one * voxelSize;
        
        // Material zuweisen und Farbe setzen
        MeshRenderer renderer = voxel.GetComponent<MeshRenderer>();
        renderer.material = new Material(voxelMaterial);
        renderer.material.color = color;
    }
}
