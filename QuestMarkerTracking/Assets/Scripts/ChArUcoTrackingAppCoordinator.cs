// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Assertions;
using PassthroughCameraSamples;
using UnityEngine.UI;

namespace TryAR.MarkerTracking
{
    /// <summary>
    /// Coordinates the AR marker tracking application, handling camera initialization,
    /// marker detection, and visualization management.
    /// </summary>
    [MetaCodeSample("PassthroughCameraApiSamples-MarkerTracking")]
    public class ChArUcoTrackingAppCoordinator : MonoBehaviour
    {
        /// <summary>
        /// Serializable class for mapping marker IDs to GameObjects in the Inspector.
        /// </summary>
        [Serializable]
        public class MarkerGameObjectPair
        {
            /// <summary>
            /// The unique ID of the AR marker to track.
            /// </summary>
            public int markerId;
            
            /// <summary>
            /// The GameObject to associate with this marker.
            /// </summary>
            public GameObject gameObject;
        }

        [Header("Camera Texture View")]
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        private PassthroughCameraEye CameraEye => m_webCamTextureManager.Eye;
        private Vector2Int CameraResolution => m_webCamTextureManager.RequestedResolution;
        [SerializeField] private Transform m_cameraAnchor;
   
        [SerializeField] private Canvas m_cameraCanvas;
        [SerializeField] private RawImage m_resultRawImage;
        [SerializeField] private float m_canvasDistance = 1f;

        [Header("Marker Tracking")]
        [SerializeField] private ChArUcoMarkerTracking m_charucoMarkerTracking;
        [SerializeField, Tooltip("List of marker IDs mapped to their corresponding GameObjects")]
        private GameObject _arObject;
        private bool m_showCameraCanvas = true;

        private Texture2D m_resultTexture;

        [Header("Object Spawning")]
        [SerializeField] private Transform m_spawnedObjectsContainer;
        private List<GameObject> m_spawnedObjects = new List<GameObject>();

        // Neue Variablen für das Sammeln von Marker-Positionen
        private Dictionary<int, List<Pose>> m_collectedMarkerPoses = new Dictionary<int, List<Pose>>();
        private bool m_isCollectingPoses = false;

        /// <summary>
        /// Initializes the camera, permissions, and marker tracking system.
        /// </summary>
        private IEnumerator Start()
        {
            // Validate required components
            if (m_webCamTextureManager == null)
            {
                Debug.LogError($"PCA: {nameof(m_webCamTextureManager)} field is required " +
                            $"for the component {nameof(ChArUcoTrackingAppCoordinator)} to operate properly");
                enabled = false;
                yield break;
            }

            // Wait for camera permissions
            Assert.IsFalse(m_webCamTextureManager.enabled);
            yield return WaitForCameraPermission();

            // Initialize camera
            yield return InitializeCamera();

            // Configure UI and tracking components
            ScaleCameraCanvas();
            
            //======================================================================================
            // CORE SETUP: Initialize the marker tracking system with camera parameters
            // This configures the ArUco detection with proper camera calibration values
            // and prepares the marker-to-GameObject mapping dictionary
            //======================================================================================
            InitializeMarkerTracking();
            
            // Set initial visibility states
            m_cameraCanvas.gameObject.SetActive(m_showCameraCanvas);
            SetMarkerObjectsVisibility(!m_showCameraCanvas);
        }

        /// <summary>
        /// Waits until camera permission is granted.
        /// </summary>
        private IEnumerator WaitForCameraPermission()
        {
            while (PassthroughCameraPermissions.HasCameraPermission != true)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Initializes the camera with appropriate resolution and waits until ready.
        /// </summary>
        private IEnumerator InitializeCamera()
        {
            // Set the resolution and enable the camera manager
            m_webCamTextureManager.RequestedResolution = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye).Resolution;
            m_webCamTextureManager.enabled = true;

            // Wait until the camera texture is available
            while(m_webCamTextureManager.WebCamTexture == null)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Updates camera poses, detects markers, and handles input for toggling visualization mode.
        /// </summary>
        private void Update()
        {
            // Skip if camera or tracking system isn't ready
            if (m_webCamTextureManager.WebCamTexture == null || !m_charucoMarkerTracking.IsReady)
                return;

            // Toggle between camera view and AR visualization on button press
            HandleVisualizationToggle();
            
            // Handle object spawning and deletion
            HandleObjectSpawningAndDeletion();
            
            // Update tracking and visualization
            UpdateCameraPoses();
            
            //======================================================================================
            // CORE FUNCTIONALITY: Process marker detection and positioning of 3D objects
            // This is where ArUco markers are detected in the camera frame and 3D objects
            // are positioned in the scene according to marker positions
            //======================================================================================
            ProcessMarkerTracking();
        }

        /// <summary>
        /// Handles button input to toggle between camera view and AR visualization.
        /// </summary>
        private void HandleVisualizationToggle()
        {
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                m_showCameraCanvas = !m_showCameraCanvas;
                m_cameraCanvas.gameObject.SetActive(m_showCameraCanvas);
                SetMarkerObjectsVisibility(!m_showCameraCanvas);
            }
        }

        /// <summary>
        /// Handles spawning objects with Y button and deleting with left thumbstick click
        /// </summary>
        private void HandleObjectSpawningAndDeletion()
        {
            // Sammle Marker-Positionen, wenn Y Button gedrückt wird
            if (OVRInput.Get(OVRInput.Button.Two))
            {
                if (!m_isCollectingPoses)
                {
                    // Starte das Sammeln, wenn der Button gerade gedrückt wurde
                    StartCollectingMarkerPoses();
                }
                else
                {
                    // Sammle kontinuierlich Posen, während der Button gehalten wird
                    CollectCurrentMarkerPoses();
                }
            }
            else if (m_isCollectingPoses)
            {
                // Button wurde losgelassen, spawne Objekte mit gemittelten Positionen
                SpawnMarkerObjectsWithAveragedPoses();
                m_isCollectingPoses = false;
            }
            
            // Delete all spawned objects when left thumbstick is clicked
            if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick))
            {
                DeleteAllSpawnedObjects();
            }
        }
        
        /// <summary>
        /// Startet das Sammeln von Marker-Positionen
        /// </summary>
        private void StartCollectingMarkerPoses()
        {
            m_collectedMarkerPoses.Clear();
            m_isCollectingPoses = true;
            Debug.Log("Started collecting marker poses...");
        }
        
        /// <summary>
        /// Sammelt die aktuellen Positionen und Rotationen aller sichtbaren Marker
        /// </summary>
        private void CollectCurrentMarkerPoses()
        {
            foreach (var pair in m_markerGameObjectDictionary)
            {
                int markerId = pair.Key;
                GameObject markerObject = pair.Value;
                
                // Überspringe, wenn das Marker-Objekt nicht aktiv ist
                if (markerObject == null || !markerObject.activeSelf)
                    continue;
                    
                // Erstelle eine neue Pose aus der aktuellen Position und Rotation
                Pose currentPose = new Pose(markerObject.transform.position, markerObject.transform.rotation);
                
                // Füge die Pose zur Liste für diesen Marker hinzu
                if (!m_collectedMarkerPoses.ContainsKey(markerId))
                {
                    m_collectedMarkerPoses[markerId] = new List<Pose>();
                }
                
                m_collectedMarkerPoses[markerId].Add(currentPose);
            }
        }
        
        /// <summary>
        /// Berechnet die durchschnittliche Position und Rotation für jeden Marker
        /// und spawnt Objekte an diesen Positionen
        /// </summary>
        private void SpawnMarkerObjectsWithAveragedPoses()
        {
            foreach (var pair in m_collectedMarkerPoses)
            {
                int markerId = pair.Key;
                List<Pose> poses = pair.Value;
                
                // Überspringe, wenn keine Posen gesammelt wurden
                if (poses.Count == 0)
                    continue;
                    
                // Berechne die durchschnittliche Position
                Vector3 avgPosition = Vector3.zero;
                foreach (var pose in poses)
                {
                    avgPosition += pose.position;
                }
                avgPosition /= poses.Count;
                
                // Berechne die durchschnittliche Rotation
                Quaternion avgRotation = AverageQuaternions(poses.ConvertAll(p => p.rotation));
                
                // Hole das zugehörige Marker-Objekt
                if (m_markerGameObjectDictionary.TryGetValue(markerId, out GameObject markerObject))
                {
                    // Erstelle eine Kopie des Marker-Objekts an der gemittelten Position
                    GameObject copy = Instantiate(markerObject, avgPosition, avgRotation);
                    
                    // Generiere eine zufällige Farbe (Alpha bleibt unverändert)
                    Color randomColor = new Color(
                        UnityEngine.Random.value,  // Rot
                        UnityEngine.Random.value,  // Grün
                        UnityEngine.Random.value   // Blau
                    );
                    
                    // Wende die Farbe auf alle Renderer-Komponenten an
                    var rendererList = copy.GetComponentsInChildren<Renderer>(true);
                    foreach (var renderer in rendererList)
                    {
                        renderer.enabled = true;
                        foreach (var material in renderer.materials)
                        {
                            material.color = new Color(
                                randomColor.r,
                                randomColor.g,
                                randomColor.b,
                                0.6f  // Setze Transparenz auf 60%
                            );
                        }
                    }
                    
                    // Füge zum Container hinzu, falls verfügbar
                    if (m_spawnedObjectsContainer != null)
                    {
                        copy.transform.SetParent(m_spawnedObjectsContainer);
                    }
                    
                    // Zur Liste hinzufügen
                    m_spawnedObjects.Add(copy);
                    
                    Debug.Log($"Spawned object for marker ID {markerId} at averaged position from {poses.Count} samples");
                }
            }
            
            // Leere die gesammelten Posen
            m_collectedMarkerPoses.Clear();
        }
        
        /// <summary>
        /// Berechnet den Durchschnitt mehrerer Quaternions
        /// </summary>
        private Quaternion AverageQuaternions(List<Quaternion> quaternions)
        {
            if (quaternions.Count == 0)
                return Quaternion.identity;
                
            if (quaternions.Count == 1)
                return quaternions[0];
                
            // Implementierung basierend auf der Methode von Markley et al.
            Vector4 summedQuaternion = Vector4.zero;
            
            foreach (var quat in quaternions)
            {
                // Stelle sicher, dass alle Quaternions im gleichen Halbraum sind
                float weight = 1.0f / quaternions.Count;
                float dot = Quaternion.Dot(quat, quaternions[0]);
                
                if (dot < 0)
                {
                    summedQuaternion += new Vector4(-quat.x, -quat.y, -quat.z, -quat.w) * weight;
                }
                else
                {
                    summedQuaternion += new Vector4(quat.x, quat.y, quat.z, quat.w) * weight;
                }
            }
            
            // Normalisiere das Ergebnis
            float magnitude = Mathf.Sqrt(summedQuaternion.x * summedQuaternion.x +
                                        summedQuaternion.y * summedQuaternion.y +
                                        summedQuaternion.z * summedQuaternion.z +
                                        summedQuaternion.w * summedQuaternion.w);
                                        
            if (magnitude > 0.0001f)
            {
                summedQuaternion /= magnitude;
            }
            else
            {
                return Quaternion.identity;
            }
            
            return new Quaternion(summedQuaternion.x, summedQuaternion.y, summedQuaternion.z, summedQuaternion.w);
        }

        /// <summary>
        /// Deletes all previously spawned marker object copies
        /// </summary>
        private void DeleteAllSpawnedObjects()
        {
            foreach (var obj in m_spawnedObjects)
            {
                if (obj != null)
                {
                    Destroy(obj);
                }
            }
            
            m_spawnedObjects.Clear();
            Debug.Log("Deleted all spawned objects");
        }

        /// <summary>
        /// Performs marker detection and pose estimation.
        /// This is the core functionality that processes camera frames to detect markers
        /// and position virtual objects in 3D space.
        /// </summary>
        private void ProcessMarkerTracking()
        {
            // Step 1: Detect ArUco markers in the current camera frame
            m_charucoMarkerTracking.DetectMarker(m_webCamTextureManager.WebCamTexture, m_resultTexture);
            
            // Step 2: Estimate the pose of markers and position 3D objects accordingly
            // This maps the 2D marker positions to 3D space using the camera parameters
            m_charucoMarkerTracking.EstimatePose(_arObject, m_cameraAnchor);
        }

        /// <summary>
        /// Toggles the visibility of all marker-associated GameObjects in the dictionary.
        /// </summary>
        /// <param name="isVisible">Whether the marker objects should be visible or not.</param>
        private void SetMarkerObjectsVisibility(bool isVisible)
        {
            // Toggle visibility for all GameObjects in the marker dictionary
            if (_arObject != null)
            {
                var rendererList = _arObject.GetComponentsInChildren<Renderer>(true);
                foreach (var meshRenderer in rendererList)
                {
                    meshRenderer.enabled = isVisible;
                }
            }   
        }
    
        /// <summary>
        /// Initializes the marker tracking system with camera parameters and builds the marker dictionary.
        /// This method configures the ArUco marker detection system with the correct camera parameters
        /// for accurate pose estimation.
        /// </summary>
        private void InitializeMarkerTracking()
        {
            // Create spawn container if it doesn't exist
            if (m_spawnedObjectsContainer == null)
            {
                GameObject container = new GameObject("SpawnedObjectsContainer");
                m_spawnedObjectsContainer = container.transform;
            }
            
            // Step 1: Set up camera parameters for tracking
            // These intrinsic parameters are essential for accurate marker pose estimation
            var intrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
            var cx = intrinsics.PrincipalPoint.x;  // Principal point X (optical center)
            var cy = intrinsics.PrincipalPoint.y;  // Principal point Y (optical center)
            var fx = intrinsics.FocalLength.x;     // Focal length X
            var fy = intrinsics.FocalLength.y;     // Focal length Y
            var width = intrinsics.Resolution.x;   // Image width
            var height = intrinsics.Resolution.y;  // Image height
            
            // Initialize the ArUco tracking with camera parameters
            m_charucoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);
            
            // Step 2: Set up texture for visualization
            ConfigureResultTexture(width, height);
        }



        /// <summary>
        /// Configures the texture for displaying camera and tracking results.
        /// </summary>
        /// <param name="width">Width of the camera resolution</param>
        /// <param name="height">Height of the camera resolution</param>
        private void ConfigureResultTexture(int width, int height)
        {
            int divideNumber = m_charucoMarkerTracking.DivideNumber;
            m_resultTexture = new Texture2D(width/divideNumber, height/divideNumber, TextureFormat.RGB24, false);
            m_resultRawImage.texture = m_resultTexture;
        }

        /// <summary>
        /// Calculates the dimensions of the canvas based on the distance from the camera origin and the camera resolution.
        /// </summary>
        private void ScaleCameraCanvas()
        {
            var cameraCanvasRectTransform = m_cameraCanvas.GetComponentInChildren<RectTransform>();
            
            // Calculate field of view based on camera parameters
            var leftSidePointInCamera = PassthroughCameraUtils.ScreenPointToRayInCamera(CameraEye, new Vector2Int(0, CameraResolution.y / 2));
            var rightSidePointInCamera = PassthroughCameraUtils.ScreenPointToRayInCamera(CameraEye, new Vector2Int(CameraResolution.x, CameraResolution.y / 2));
            var horizontalFoVDegrees = Vector3.Angle(leftSidePointInCamera.direction, rightSidePointInCamera.direction);
            var horizontalFoVRadians = horizontalFoVDegrees / 180 * Math.PI;
            
            // Calculate canvas size to match camera view
            var newCanvasWidthInMeters = 2 * m_canvasDistance * Math.Tan(horizontalFoVRadians / 2);
            var localScale = (float)(newCanvasWidthInMeters / cameraCanvasRectTransform.sizeDelta.x);
            cameraCanvasRectTransform.localScale = new Vector3(localScale, localScale, localScale);
        }

        /// <summary>
        /// Updates the positions and rotations of camera-related transforms based on head and camera poses.
        /// </summary>
        private void UpdateCameraPoses()
        {
            // Get current head pose
            var headPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
            
            // Update camera anchor position and rotation
            var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
            m_cameraAnchor.position = cameraPose.position;
            m_cameraAnchor.rotation = cameraPose.rotation;

            // Position the canvas in front of the camera
            m_cameraCanvas.transform.position = cameraPose.position + cameraPose.rotation * Vector3.forward * m_canvasDistance;
            m_cameraCanvas.transform.rotation = cameraPose.rotation;
        }
    }
}
