using UnityEngine;

public class ObjectRotator : MonoBehaviour
{
    [Header("Rotation Settings")]
    [SerializeField] private Vector3 rotationSpeed = new Vector3(0, 30, 0); // Grad pro Sekunde
    [SerializeField] private Space rotationSpace = Space.Self;
    
    [Header("Additional Effects")]
    [SerializeField] private bool pingPongScale = false;
    [SerializeField] private float scaleSpeed = 0.5f;
    [SerializeField] private float minScale = 0.8f;
    [SerializeField] private float maxScale = 1.2f;
    
    private Vector3 originalScale;
    private float scaleTimer = 0f;
    
    private void Start()
    {
        originalScale = transform.localScale;
    }
    
    void Update()
    {
        // Rotation anwenden
        transform.Rotate(rotationSpeed * Time.deltaTime, rotationSpace);
        
        // Optionaler Ping-Pong Skalierungseffekt
        if (pingPongScale)
        {
            scaleTimer += Time.deltaTime * scaleSpeed;
            
            // Ping-Pong zwischen 0 und 1
            float scaleFactor = Mathf.PingPong(scaleTimer, 1f);
            
            // Interpoliere zwischen min und max Scale
            float currentScale = Mathf.Lerp(minScale, maxScale, scaleFactor);
            
            // Wende die neue Skalierung an
            transform.localScale = originalScale * currentScale;
        }
    }
} 