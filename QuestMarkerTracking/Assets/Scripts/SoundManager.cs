using UnityEngine;

public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance { get; private set; }
    
    [Header("Sound Effects")]
    public AudioClip droneExplosionSound;
    public AudioClip voxelShootSound;
    // Weitere Sounds...
    
    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }
    
    public void PlaySoundAtPosition(AudioClip clip, Vector3 position, float volume = 1.0f)
    {
        if (clip != null)
        {
            AudioSource.PlayClipAtPoint(clip, position, volume);
        }
    }
} 