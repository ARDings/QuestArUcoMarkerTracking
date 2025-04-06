using UnityEngine;

public class DynamicDroneBuilder : MonoBehaviour
{
    public float radius = 0.3f;
    public float cubeSize = 0.05f;
    public float density = 1f;
    public Material droneMaterial;
    public AudioClip droneDestroySound;

    public GameObject BuildDrone()
    {
        GameObject drone = new GameObject("VoxelDrone");
        Rigidbody rb = drone.AddComponent<Rigidbody>();
        rb.isKinematic = true;

        SphereCollider sc = drone.AddComponent<SphereCollider>();
        sc.isTrigger = true;

        DroneAI ai = drone.AddComponent<DroneAI>();
        ai.userHead = Camera.main.transform;
        ai.orbitRadius = Random.Range(1.5f, 3.5f);
        ai.orbitSpeed = Random.Range(15f, 45f);
        ai.destroySound = droneDestroySound;

        for (float x = -radius; x <= radius; x += cubeSize * density)
        {
            for (float y = -radius; y <= radius; y += cubeSize * density)
            {
                for (float z = -radius; z <= radius; z += cubeSize * density)
                {
                    Vector3 pos = new Vector3(x, y, z);
                    if (pos.magnitude <= radius)
                    {
                        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        cube.transform.SetParent(drone.transform);
                        cube.transform.localPosition = pos;
                        cube.transform.localScale = Vector3.one * cubeSize;
                        Destroy(cube.GetComponent<Collider>());

                        if (droneMaterial != null)
                        {
                            var baseColor = droneMaterial.color;
                            float variance = 0.1f;
                            Color variedColor = new Color(
                                Mathf.Clamp01(baseColor.r + Random.Range(-variance, variance)),
                                Mathf.Clamp01(baseColor.g + Random.Range(-variance, variance)),
                                Mathf.Clamp01(baseColor.b + Random.Range(-variance, variance))
                            );
                            Material newMat = new Material(droneMaterial);
                            newMat.color = variedColor;
                            cube.GetComponent<Renderer>().material = newMat;
                        }
                    }
                }
            }
        }

        return drone;
    }
}
