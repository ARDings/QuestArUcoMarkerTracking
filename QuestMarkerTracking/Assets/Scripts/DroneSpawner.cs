using System.Collections;
using UnityEngine;

public class DroneSpawner : MonoBehaviour
{
    public Transform userHead;
    public float spawnRadius = 3f;
    public float spawnInterval = 30f;
    public DynamicDroneBuilder builder;

    private void Start()
    {
        StartCoroutine(SpawnRoutine());
    }

    IEnumerator SpawnRoutine()
    {
        while (true)
        {
            SpawnDrone();
            yield return new WaitForSeconds(spawnInterval);
        }
    }

    void SpawnDrone()
    {
        Vector3 offset = Random.onUnitSphere * spawnRadius;
        offset.y = 0; // bleibt auf Augenhöhe
        Vector3 spawnPos = userHead.position + offset;
        spawnPos.y = userHead.position.y;

        GameObject drone = builder.BuildDrone();
        drone.transform.position = spawnPos;
        drone.transform.rotation = Quaternion.LookRotation(userHead.position - spawnPos);
    }
}
