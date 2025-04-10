using UnityEngine;
using System.Collections;

public class DroneAI : MonoBehaviour
{
    public Transform userHead;
    public float orbitSpeed = 10f;
    public float orbitRadius = 2f;
    public float updateTargetDelay = 2f;
    public float shootInterval = 10f;
    public float voxelProjectileSpeed = 6f;
    
    [Header("Sound Effects")]
    public AudioClip explosionSound;
    public float explosionVolume = 1.0f;
    [SerializeField] private bool useGlobalAudioSource = false;
    [SerializeField] private AudioSource customAudioSource;

    public AudioClip destroySound;
    private AudioSource audioSource;

    private float angle;
    private Vector3 cachedUserPosition;

    void Start()
    {
        if (userHead == null)
            userHead = Camera.main.transform;

        StartCoroutine(UpdateTargetPosition());
        StartCoroutine(ShootVoxels());
        
        // Stelle sicher, dass wir einen AudioSource haben, wenn wir keinen globalen verwenden
        if (!useGlobalAudioSource && customAudioSource == null)
        {
            customAudioSource = gameObject.AddComponent<AudioSource>();
        }

        // AudioSource hinzufügen
        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.spatialBlend = 1f; // 3D Sound
    }

    void Update()
    {
        angle += orbitSpeed * Time.deltaTime;
        float rad = angle * Mathf.Deg2Rad;

        Vector3 offset = new Vector3(Mathf.Cos(rad), 0, Mathf.Sin(rad)) * orbitRadius;
        Vector3 targetPos = cachedUserPosition + offset;
        targetPos.y = cachedUserPosition.y;

        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * 2f);
        
        // Berechnung der Rotation mit zusätzlichen Winkeln
        Vector3 directionToCamera = (cachedUserPosition - transform.position).normalized;
        directionToCamera.y = 0;
        Quaternion baseRotation = Quaternion.LookRotation(-directionToCamera);
        
        // Kombiniere die Rotationen: erst Basis, dann Z-Rotation, dann Y-Rotation
        transform.rotation = baseRotation * Quaternion.Euler(0, 70, 90);
    }

    IEnumerator UpdateTargetPosition()
    {
        while (true)
        {
            if (userHead != null)
                cachedUserPosition = userHead.position;

            yield return new WaitForSeconds(updateTargetDelay);
        }
    }

    IEnumerator ShootVoxels()
    {
        while (true)
        {
            yield return new WaitForSeconds(shootInterval);

            if (transform.childCount == 0 || userHead == null) continue;

            int shots = Mathf.Min(4, transform.childCount);

            for (int i = 0; i < shots; i++)
            {
                int index = Random.Range(0, transform.childCount);
                Transform voxel = transform.GetChild(index);
                voxel.SetParent(null);

                Rigidbody rb = voxel.gameObject.AddComponent<Rigidbody>();
                rb.mass = 0.01f;

                // Stelle sicher, dass der Voxel einen Collider hat
                if (voxel.GetComponent<Collider>() == null)
                {
                    voxel.gameObject.AddComponent<BoxCollider>();
                }

                // Schussrichtung mit leichtem Versatz
                Vector3 dir = (userHead.position - voxel.position).normalized;
                dir += new Vector3(Random.Range(-0.2f, 0.2f), Random.Range(-0.1f, 0.1f), Random.Range(-0.2f, 0.2f));
                dir.Normalize();

                rb.linearVelocity = dir * voxelProjectileSpeed;

                // Optionale Explosionforce für mehr "Wumms"
                rb.AddExplosionForce(3f, voxel.position - dir, 1f);
            }
        }
    }

    void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("sword"))
        {
            Explode();
        }
    }

    void Explode()
    {
        // Spiele den Explosionssound ab
        PlayExplosionSound();
        
        foreach (Transform child in transform)
        {
            child.SetParent(null);

            if (child.GetComponent<Collider>() == null)
            {
                child.gameObject.AddComponent<BoxCollider>();
            }

            Rigidbody rb = child.gameObject.AddComponent<Rigidbody>();
            rb.mass = 0.01f;
            
            // Stärkere Explosionskraft und größerer Radius für mehr Teilchen-Streuung
            rb.AddExplosionForce(10f, transform.position, 3f);
            
     

            // Optional: Zerstöre die Voxel nach einiger Zeit
            Destroy(child.gameObject, Random.Range(2f, 4f));
        }

        Destroy(gameObject); // Drohne selbst zerstören
    }
    
    void PlayExplosionSound()
    {
        if (explosionSound == null)
        {
            Debug.LogWarning("Kein Explosionssound zugewiesen!");
            return;
        }
        
        if (useGlobalAudioSource)
        {
            // Verwende AudioSource.PlayClipAtPoint für einen globalen Sound
            AudioSource.PlayClipAtPoint(explosionSound, transform.position, explosionVolume);
        }
        else if (customAudioSource != null)
        {
            // Verwende den benutzerdefinierten AudioSource
            customAudioSource.clip = explosionSound;
            customAudioSource.volume = explosionVolume;
            customAudioSource.Play();
            
            // Verhindere, dass der Sound abgeschnitten wird, wenn das GameObject zerstört wird
            customAudioSource.transform.SetParent(null);
            Destroy(customAudioSource.gameObject, explosionSound.length);
        }
    }

    // Diese Methode wird aufgerufen, wenn die Drohne zerstört wird
    void OnDestroy()
    {
        if (destroySound != null)
        {
            // Sound an der letzten Position der Drohne abspielen
            AudioSource.PlayClipAtPoint(destroySound, transform.position);
        }
    }
}
