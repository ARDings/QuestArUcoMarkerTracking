using UnityEngine;

public class EmissionOffsetAnimator : MonoBehaviour
{
    [Header("Animation Settings")]
    [SerializeField] private float scrollSpeed = 0.5f;
    [SerializeField] private bool reverseDirection = false;

    [Header("Offset Limits")]
    [SerializeField] private float minYOffset = 0f;
    [SerializeField] private float maxYOffset = 20f;
    
    [Header("Target Settings")]
    [SerializeField] private bool animateMainEmission = true;
    [SerializeField] private bool animateSecondaryMaps = false;

    private Material material;
    private Vector2 emissionOffset;
    private Vector2 secondaryOffset;

    void Start()
    {
        // Hole das Material vom Renderer
        Renderer renderer = GetComponent<Renderer>();
        if (renderer != null && renderer.sharedMaterial != null)
        {
            // Erstelle eine Instanz des Materials
            material = new Material(renderer.sharedMaterial);
            renderer.material = material;
            
            // Hole die aktuellen Offset-Werte
            if (animateMainEmission)
            {
                // Die Emission-Map im Standard-Shader
                emissionOffset = material.GetTextureOffset("_EmissionMap");
                Debug.Log($"EmissionAnimator: Initial emission offset: {emissionOffset}");
            }
            
            if (animateSecondaryMaps)
            {
                // Die Secondary Maps
                secondaryOffset = material.GetTextureOffset("_DetailAlbedoMap");
                Debug.Log($"EmissionAnimator: Initial secondary offset: {secondaryOffset}");
            }
        }
        else
        {
            Debug.LogError("EmissionAnimator: No Renderer with material found on this GameObject!");
            enabled = false;
        }
    }

    void Update()
    {
        if (material != null)
        {
            float direction = reverseDirection ? -1 : 1;
            float deltaY = direction * scrollSpeed * Time.deltaTime;
            
            // Aktualisiere Emission-Offset
            if (animateMainEmission)
            {
                emissionOffset.y += deltaY;
                if (emissionOffset.y > maxYOffset) emissionOffset.y = minYOffset;
                if (emissionOffset.y < minYOffset) emissionOffset.y = maxYOffset;
                
                material.SetTextureOffset("_EmissionMap", emissionOffset);
            }
            
            // Aktualisiere Secondary-Offset
            if (animateSecondaryMaps)
            {
                secondaryOffset.y += deltaY;
                if (secondaryOffset.y > maxYOffset) secondaryOffset.y = minYOffset;
                if (secondaryOffset.y < minYOffset) secondaryOffset.y = maxYOffset;
                
                material.SetTextureOffset("_DetailAlbedoMap", secondaryOffset);
                material.SetTextureOffset("_DetailNormalMap", secondaryOffset);
            }
        }
    }

    void OnDestroy()
    {
        if (material != null)
        {
            if (Application.isPlaying)
                Destroy(material);
            else
                DestroyImmediate(material);
        }
    }
} 