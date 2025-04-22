using UnityEngine;

public class DualCameraTest : MonoBehaviour
{
    [SerializeField] private CameraManager _leftEyeManager;
    [SerializeField] private CameraManager _rightEyeManager;
    [SerializeField] private Material _leftEyeMaterial;
    [SerializeField] private Material _rightEyeMaterial;

    void Start()
    {
        // Optional: Shader-Einstellungen für YUV->RGB
        _leftEyeMaterial.EnableKeyword("_COLORSPACE_YUV");
        _rightEyeMaterial.EnableKeyword("_COLORSPACE_YUV");
    }


} 