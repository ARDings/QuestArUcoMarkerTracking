// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
using System.Collections;
using System.Collections.Generic;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Assertions;
using PassthroughCameraSamples;
using UnityEngine.UI;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityUtils;
using TryAR.ColorTracking;  // Für die ColorObject Klasse
using System.Threading;
using System.Collections.Concurrent;
using OpenCVForUnity.Calib3dModule;
using System.Linq;

namespace TryAR.MarkerTracking
{
    /// <summary>
    /// Coordinates the AR marker tracking application, handling camera initialization,
    /// marker detection, and visualization management.
    /// </summary>
    [MetaCodeSample("PassthroughCameraApiSamples-MarkerTracking")]
    public class ArUcoTrackingAppCoordinator : MonoBehaviour
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

        [Header("Camera")]
        [SerializeField] private SimpleCameraPreview m_cameraPreview;
        private PassthroughCameraEye CameraEye => PassthroughCameraEye.Left;
        [SerializeField] private Transform m_cameraAnchor;

        [Header("Marker Tracking")]
        [SerializeField] private ArUcoMarkerTracking m_arucoMarkerTracking;
        [SerializeField, Tooltip("List of marker IDs mapped to their corresponding GameObjects")]
        private List<MarkerGameObjectPair> m_markerGameObjectPairs = new List<MarkerGameObjectPair>();
        private Dictionary<int, GameObject> m_markerGameObjectDictionary = new Dictionary<int, GameObject>();

        [Header("Object Spawning")]
        [SerializeField] private Transform m_spawnedObjectsContainer;
        private List<GameObject> m_spawnedObjects = new List<GameObject>();

        // Neue Variablen für das Sammeln von Marker-Positionen
        private Dictionary<int, List<Pose>> m_collectedMarkerPoses = new Dictionary<int, List<Pose>>();
        private bool m_isCollectingPoses = false;

        [Header("Tracking Mode")]
        [SerializeField] private bool m_enableMarkerTracking = true;
        [SerializeField] private bool m_enableColorTracking = true;

        [Header("Color Tracking")]
        [SerializeField] private bool m_showColorDebugView = true;
        [SerializeField, Tooltip("Der tatsächliche Durchmesser des Balls in Metern (z.B. 0.1 für einen 10cm Ball)")]
        private float m_ballDiameterInMeters = 0.1f; // 10cm Standard
        [SerializeField] private GameObject m_ballVisualization;

        [Header("Pink Ball HSV Settings")]
        [SerializeField, Tooltip("HSV Minimum Werte (H: 0-180, S: 0-255, V: 0-255)")]
        private Vector3 m_pinkHSVMin = new Vector3(140, 50, 150);  // Helleres Pink, weniger Sättigung
        [SerializeField, Tooltip("HSV Maximum Werte (H: 0-180, S: 0-255, V: 0-255)")]
        private Vector3 m_pinkHSVMax = new Vector3(175, 255, 255);  // Breiterer Farbbereich für verschiedene Lichtverhältnisse

        private ColorObject m_pinkBall;
        private Mat m_rgbMat;
        private Mat m_hsvMat;
        private Mat m_thresholdMat;

        [Header("HSV Control Settings")]
        [SerializeField] private float m_hsvAdjustSpeed = 2f;  // Geschwindigkeit der Anpassung
        [SerializeField] private bool m_showHSVDebug = true;   // HSV Werte im Debug anzeigen

        [Header("Ball Position Settings")]
        [SerializeField] private Vector3 m_ballOffset = Vector3.zero;
        [SerializeField] private bool m_showOffsetDebug = true;
        [SerializeField] private float m_visualScaleFactor = 2.0f; // Visueller Skalierungsfaktor
        private Vector3 m_controllerStartPosition;
        private bool m_isSettingOffset = false;

        [Header("Performance Settings")]
        [SerializeField] private int m_processingDivider = 1;

        // Neue Felder für Threading
        private Thread m_processingThread;
        private ConcurrentQueue<RenderTextureData> m_frameQueue;
        private ConcurrentQueue<BallDetectionResult> m_resultQueue;
        private volatile bool m_isProcessing;
        private object m_matLock = new object();

        // Add a struct to store camera poses with timestamps
        private struct TimestampedCameraPose
        {
            public long Timestamp;  // Using sensor timestamp as reference
            public Vector3 Position;
            public Quaternion Rotation;
        }

        // Add a ring buffer to store recent camera poses
        [SerializeField, Tooltip("Number of camera poses to store in history")]
        private int m_cameraPoseHistorySize = 60; // Adjust based on your framerate and needed history length
        private TimestampedCameraPose[] m_cameraPoseHistory;
        private int m_cameraPoseHistoryIndex = 0;

        // Strukturen für Thread-sichere Datenübergabe
        private struct RenderTextureData
        {
            public byte[] TextureBytes;
            public int Width;
            public int Height;
            public Matrix4x4 CameraToWorldMatrix;
            public PassthroughCameraIntrinsics CameraIntrinsics;
        }

        private struct BallDetectionResult
        {
            public bool BallFound;
            public Vector3 WorldPosition;
            public float Diameter;
        }

        // Add these fields to track time synchronization
        private bool m_timeOffsetInitialized = false;
        private long m_systemToUnityTimeOffsetNs = 0;


        // Add fields to track skipped frames and adaptive threshold
        public float m_currentTimeDifferenceThresholdMs;
        
        // Add a field to track the last processed frame timestamp
        private long m_lastProcessedFrameTimestamp = 0;
        
        // Add frame statistics tracking
        private int m_processedFrameCount = 0;
        private int m_skippedFrameCount = 0;

        [Header("Grid Board Configuration")]
        [SerializeField, Tooltip("Whether to require all markers in the grid board to be detected simultaneously")]
        private bool m_requireAllGridBoardMarkers = true;
        [SerializeField, Tooltip("Number of markers in the grid board")]
        private int m_expectedMarkerCount = 6;
        [SerializeField, Tooltip("IDs of markers in the grid board (0-5 for a 6-marker board)")]
        private List<int> m_gridBoardMarkerIds = new List<int> { 0, 1, 2, 3, 4, 5 };
        [SerializeField, Tooltip("Size of each marker in millimeters")]
        private float m_markerSizeInMm = 50f;

        // New fields for pose averaging
        [Header("Pose Averaging")]
        [SerializeField, Tooltip("Number of high-quality poses to collect and average")]
        private int m_posesToCollect = 20;
        private Dictionary<int, List<Pose>> m_collectedPoses = new Dictionary<int, List<Pose>>();
        private Dictionary<int, Pose> m_averagedPoses = new Dictionary<int, Pose>();
        private Dictionary<int, Pose> m_previousAveragedPoses = new Dictionary<int, Pose>();
        private bool m_hasSufficientPoses = false;
        [SerializeField, Range(0, 1), Tooltip("Weight of previous average (0=only new poses, 1=only old poses)")]
        private float m_previousAverageWeight = 0.3f;
        [SerializeField, Tooltip("Reset pose collection after applying averaged poses")]
        private bool m_resetCollectionAfterApplying = true;

        // Track detected markers in the current frame
        private HashSet<int> m_detectedMarkersInCurrentFrame = new HashSet<int>();
        private bool m_allGridMarkersDetected = false;

        [Header("Time Synchronization")]
        [SerializeField, Tooltip("Manual adjustment offset for timestamp matching (positive values look further back in time, negative values look forward)")]
        private float m_manualTimeOffsetMs = 0.0f;  // Default to no offset
        [SerializeField, Tooltip("Adjustment speed for time offset")]
        private float m_timeOffsetAdjustSpeed = 1.0f;
        private bool m_isAdjustingTimeOffset = false;

        [Header("Quality Indicator")]
        [SerializeField] private GameObject m_qualityIndicator;
        [SerializeField] private Material m_excellentQualityMaterial;
        [SerializeField] private Material m_goodQualityMaterial;
        [SerializeField] private Material m_moderateQualityMaterial;
        [SerializeField] private Material m_poorQualityMaterial;

        [Header("Debug")]
        [SerializeField] private AudioClip m_positionUpdateSound;
        [SerializeField] private AudioSource m_audioSource;
        [SerializeField] private bool m_unparentMarkersOnStart = true;
        [SerializeField] private bool m_freezePhysicsOnMarkers = true;

        [Header("Debugging")]
        [SerializeField] private bool m_showMarkerPositionVisualizations = true;
        [SerializeField] private Material m_markerVisualizationMaterial;
        [SerializeField] private Material m_averagedPositionMaterial;
        private Dictionary<int, GameObject> m_markerVisualizations = new Dictionary<int, GameObject>();
        private Dictionary<int, GameObject> m_averagedPositionVisualizations = new Dictionary<int, GameObject>();

        [Header("Board Visualization")]
        [SerializeField] private GameObject m_boardPrefab;
        [SerializeField] private bool m_showBoardVisualization = true;
        private GameObject m_boardVisualization;

        [Header("Board Rotation Limits")]
        [SerializeField] private float m_maxRotationSpeedDegreesPerSecond = 30f; // Maximale Drehgeschwindigkeit in Grad/Sekunde
        [SerializeField] private bool m_enableRotationSpeedLimit = true;
        private float m_lastYRotation = 0f;
        private float m_lastRotationUpdateTime = 0f;
        private bool m_initialRotationSet = false;

        // Erhöhe den Schwellenwert für die Zeitdifferenz, da wir jetzt nur 5 FPS haben
        // Bei 5 FPS haben wir 200ms zwischen den Frames, also sollten wir einen höheren Schwellenwert setzen

        // Am Anfang der Klasse
        private GameObject _tempPoseObject;

        /// <summary>
        /// Initializes the camera, permissions, and marker tracking system.
        /// </summary>
        private IEnumerator Start()
        {
            if (m_cameraPreview == null)
            {
                Debug.LogError($"PCA: {nameof(m_cameraPreview)} field is required");
                enabled = false;
                yield break;
            }

            while (!m_cameraPreview.AreCamerasReady)
            {
                yield return null;
            }

            InitializeMarkerTracking();
            SetMarkerObjectsVisibility(true);

            // Initialisiere Ball-Tracking
            m_pinkBall = new ColorObject("pink");
            UpdatePinkHSVValues();
            InitializeBallTracking();

            // Initialize threading components
            m_frameQueue = new ConcurrentQueue<RenderTextureData>();
            m_resultQueue = new ConcurrentQueue<BallDetectionResult>();
            m_isProcessing = true;
            
            // Start processing thread
            m_processingThread = new Thread(ProcessingThreadFunction);
            m_processingThread.Start();

            // Subscribe to frame timestamps for time synchronization
            SubscribeToFrameTimestamps();

            // Initialize camera pose history
            m_cameraPoseHistory = new TimestampedCameraPose[m_cameraPoseHistorySize];
            for (int i = 0; i < m_cameraPoseHistorySize; i++)
            {
                m_cameraPoseHistory[i] = new TimestampedCameraPose 
                { 
                    Timestamp = 0, 
                    Position = Vector3.zero, 
                    Rotation = Quaternion.identity 
                };
            }

            // Initialize the adaptive threshold to the base value

            if (m_unparentMarkersOnStart)
            {
                UnparentMarkerObjects();
            }
            
            if (m_freezePhysicsOnMarkers)
            {
                DisablePhysicsOnMarkerObjects();
            }

            // Das komplette Einfrieren einmal zu Beginn ausführen
            FreezeMarkerObjects();

            // Erstelle das temporäre Pose-Objekt einmalig
            _tempPoseObject = new GameObject("HistoricalCameraPose");
            _tempPoseObject.hideFlags = HideFlags.HideInHierarchy; // Verstecke es im Editor
        }

        /// <summary>
        /// Updates camera poses, detects markers, and handles input for toggling visualization mode.
        /// </summary>
        private void Update()
        {
            if (!m_cameraPreview.AreCamerasReady) return;

            // Store current camera pose with current system time
            StoreCameraPose();

            if (m_cameraPreview != null && m_cameraPreview.HasNewFrame())
            {
                // Get the new camera frame
                var frame = m_cameraPreview.GetCurrentFrame();
                
                // Skip if we've already processed this frame
                if (frame.Timestamps.SensorTimestampNs == m_lastProcessedFrameTimestamp)
                {
                    return;
                }
                m_lastProcessedFrameTimestamp = frame.Timestamps.SensorTimestampNs;

                if (m_enableMarkerTracking && m_arucoMarkerTracking.IsReady)
                {
                    if (frame.IsValid)
                    {                        
                        // Get the closest historical camera pose for this frame
                        





                        // Check if we have a decent time match before processing the frame
                        long timeDiff = Math.Abs(m_cameraPoseHistory[m_cameraPoseHistoryIndex].Timestamp - frame.Timestamps.SensorTimestampNs);
                        float timeDiffMs = timeDiff / 1000000.0f;

                        
                        
                        Transform historicalCameraTransform = GetCameraPoseForTimestamp(frame.Timestamps.SensorTimestampNs);

                        
                        if (historicalCameraTransform != null)
                        {
                            // Clear previously detected markers
                            m_detectedMarkersInCurrentFrame.Clear();
                            m_allGridMarkersDetected = false;
                            
                            // Detect markers in the current frame
                            m_arucoMarkerTracking.DetectMarker(frame.Texture);
                            
                            // After detection, check which markers were found and update our set
                            HashSet<int> detectedMarkers = m_arucoMarkerTracking.GetDetectedMarkerIds();
                            
                            // If we're requiring all grid board markers, check if all expected markers were detected
                            if (m_requireAllGridBoardMarkers)
                            {
                                m_allGridMarkersDetected = true;
                                foreach (int markerId in m_gridBoardMarkerIds)
                                {
                                    if (!detectedMarkers.Contains(markerId))
                                    {
                                        m_allGridMarkersDetected = false;
                                        break;
                                    }
                                }
                                
                                if (m_allGridMarkersDetected)
                                {
                                    // Get marker detection quality metrics
                                    var detectionMetrics = m_arucoMarkerTracking.GetDetectionQualityMetrics();
                                    
                                    // Evaluate the overall quality of the detection
                                    var qualityClass = m_arucoMarkerTracking.EvaluateDetectionQuality(detectionMetrics);
                                 
                                    
                                    // Only use high-quality detections for pose estimation
                                    bool isHighQualityDetection = (
                                                                   qualityClass == ArUcoMarkerTracking.DetectionQualityClass.Excellent
                                                                );
                                    
                                    // Only process pose estimation if detection quality is sufficient
                                    if (isHighQualityDetection)
                                    {
                                        // WICHTIG: Hier Pose-Daten nur sammeln, nicht anwenden
                                        CollectPosesWithoutAffectingTransforms(historicalCameraTransform);
                                        
                                        // Apply the averaged poses ONLY if we have enough poses
                                        if (m_hasSufficientPoses)
                                        {
                                            CalculateAveragedPoses();
                                            SetMarkerObjectsVisibility(true);
                                        }
                                    }
                                    else
                                    {
                                    }
                                }
                                else
                                {
                                }
                            }
                            else
                            {
                                // WICHTIG: Im "else"-Zweig (Original-Verhalten) dürfen wir NICHT die Position ändern!
                                // Deaktiviert: keine direkte Manipulation der Objekte durch ArUco-Tracking
                                // if (m_markerGameObjectDictionary.Count > 0)
                                // {
                                //     m_arucoMarkerTracking.EstimatePoseCanonicalMarker(
                                //         m_markerGameObjectDictionary,
                                //         historicalCameraTransform
                                //     );
                                // }
                                
                                // Stattdessen nur Posen sammeln ohne die Objekte zu beeinflussen
                                CollectPosesWithoutAffectingTransforms(historicalCameraTransform);
                            }
                        }
                        else
                        {
                            // Skip this frame as the time match is too poor
                           // Debug.LogWarning($"[Camera Sync] Skipping frame due to large time difference: {timeDiffMs}ms > {m_currentTimeDifferenceThresholdMs}ms threshold");
                        }
                    }
                }
            }

            if (m_enableColorTracking)
            {
                ProcessBallTracking(m_cameraPreview.LeftCameraTexture);
            }

            UpdateHSVControls();

            // Every 100 frames, log stats
            if ((m_processedFrameCount + m_skippedFrameCount) % 100 == 0 && 
                (m_processedFrameCount + m_skippedFrameCount) > 0)
            {
                float successRate = (float)m_processedFrameCount / (m_processedFrameCount + m_skippedFrameCount) * 100f;
            }

            // Zeige Informationen über alle erkannten Marker an
        

            // Log all detected markers regardless of quality
         



            // Log nur die letzten 5 gespeicherten Posen
            if (Time.frameCount % 30 == 0) // Auch nur alle 30 Frames
            {
                var recentPoses = m_cameraPoseHistory
                    .Where(p => p.Timestamp > 0)
                    .OrderByDescending(p => p.Timestamp)
                    .Take(5);
                    
                foreach (var pose in recentPoses)
                {
                  
                }
            }
        }

        /// <summary>
        /// Toggles the visibility of all marker-associated GameObjects in the dictionary.
        /// </summary>
        /// <param name="isVisible">Whether the marker objects should be visible or not.</param>
        private void SetMarkerObjectsVisibility(bool isVisible)
        {
            foreach (var markerObject in m_markerGameObjectDictionary.Values)
            {
                if (markerObject != null)
                {
                    var rendererList = markerObject.GetComponentsInChildren<Renderer>(true);
                    foreach (var meshRenderer in rendererList)
                    {
                        meshRenderer.enabled = isVisible;
                    }
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
            var nativeIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
            
            // Get actual camera resolution from SimpleCameraPreview
            var actualResolution = m_cameraPreview.GetCurrentResolution();
            
            // Use actual resolution instead of native intrinsics resolution
            var width = actualResolution.x / m_processingDivider;
            var height = actualResolution.y / m_processingDivider;
            
            // Scale intrinsics to match actual resolution
            float scaleX = (float)actualResolution.x / nativeIntrinsics.Resolution.x;
            float scaleY = (float)actualResolution.y / nativeIntrinsics.Resolution.y;
            
            var cx = nativeIntrinsics.PrincipalPoint.x * scaleX / m_processingDivider;
            var cy = nativeIntrinsics.PrincipalPoint.y * scaleY / m_processingDivider;
            var fx = nativeIntrinsics.FocalLength.x * scaleX / m_processingDivider;
            var fy = nativeIntrinsics.FocalLength.y * scaleY / m_processingDivider;
            
            
            m_arucoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);
            BuildMarkerDictionary();
        }

        /// <summary>
        /// Builds the dictionary mapping marker IDs to GameObjects.
        /// </summary>
        private void BuildMarkerDictionary()
        {
            m_markerGameObjectDictionary.Clear();
            foreach (var pair in m_markerGameObjectPairs)
            {
                if (pair.gameObject != null)
                {
                    m_markerGameObjectDictionary[pair.markerId] = pair.gameObject;
                }
            }
        }

        /// <summary>
        /// Updates the positions and rotations of camera-related transforms based on head and camera poses.
        /// </summary>
        private void UpdateCameraPoses()
        {
            // We still update m_cameraAnchor for other purposes
            var headPose = OVRPlugin.GetNodePoseStateImmediate(OVRPlugin.Node.Head).Pose.ToOVRPose();
            var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
            m_cameraAnchor.position = cameraPose.position;
            m_cameraAnchor.rotation = cameraPose.rotation;
        }

        private void ProcessBallTracking(RenderTexture renderTexture)
        {
            try
            {
                if (renderTexture == null) return;

                // Prepare data for processing thread
                var textureData = new RenderTextureData
                {
                    Width = renderTexture.width / m_processingDivider,
                    Height = renderTexture.height / m_processingDivider,
                    CameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye),
                    CameraToWorldMatrix = Matrix4x4.TRS(
                        PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye).position,
                        PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye).rotation,
                        Vector3.one
                    )
                };

                // Convert RenderTexture to byte array
                RenderTexture scaledRT = RenderTexture.GetTemporary(textureData.Width, textureData.Height, 0, renderTexture.format);
                Graphics.Blit(renderTexture, scaledRT);
                
                Texture2D tempTex = new Texture2D(textureData.Width, textureData.Height, TextureFormat.RGBA32, false);
                RenderTexture.active = scaledRT;
                tempTex.ReadPixels(new UnityEngine.Rect(0, 0, textureData.Width, textureData.Height), 0, 0);
                tempTex.Apply();
                
                textureData.TextureBytes = tempTex.GetRawTextureData();
                
                // Cleanup
                RenderTexture.ReleaseTemporary(scaledRT);
                Destroy(tempTex);

                // Enqueue data for processing
                m_frameQueue.Enqueue(textureData);

                // Check for results
                if (m_resultQueue.TryDequeue(out BallDetectionResult result) && result.BallFound)
                {
                    if (m_ballVisualization != null)
                    {
                        // Apply offset 
                        Vector3 finalPosition = result.WorldPosition + m_ballOffset;
                        
                        // Smooth position
                        Vector3 currentPos = m_ballVisualization.transform.position;
                        float smoothFactor = 0.2f;
                        finalPosition = Vector3.Lerp(currentPos, finalPosition, 1 - smoothFactor);
                        
                        // Apply position and scale
                        m_ballVisualization.transform.position = finalPosition;
                        m_ballVisualization.transform.localScale = Vector3.one * m_ballDiameterInMeters * m_visualScaleFactor;
                    }
                }
            }
            catch (System.Exception e)
            {
            }
        }

        private void InitializeBallTracking()
        {
            if (m_rgbMat != null) m_rgbMat.Dispose();
            if (m_hsvMat != null) m_hsvMat.Dispose();
            if (m_thresholdMat != null) m_thresholdMat.Dispose();

            m_rgbMat = new Mat();
            m_hsvMat = new Mat();
            m_thresholdMat = new Mat();
        }

        private void UpdatePinkHSVValues()
        {
            if (m_pinkBall != null)
            {
                m_pinkBall.setHSVRanges(m_pinkHSVMin, m_pinkHSVMax);
            }
        }

        private void UpdateHSVControls()
        {
            Vector2 leftStick = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick);
            Vector2 rightStick = OVRInput.Get(OVRInput.Axis2D.SecondaryThumbstick);
            bool leftTrigger = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger);
            bool rightTrigger = OVRInput.Get(OVRInput.Button.SecondaryIndexTrigger);
            bool leftGrip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger);
            bool rightGrip = OVRInput.Get(OVRInput.Button.SecondaryHandTrigger);

            // New code: Press left and right grip at the same time to adjust time offset
            if (leftGrip && rightGrip)
            {
                if (!m_isAdjustingTimeOffset)
                {
                    m_isAdjustingTimeOffset = true;
                }

                // Use right stick Y-axis to adjust time offset
                if (Mathf.Abs(rightStick.y) > 0.1f)
                {
                    m_manualTimeOffsetMs += rightStick.y * m_timeOffsetAdjustSpeed;
                }
            }
            else if (m_isAdjustingTimeOffset)
            {
                m_isAdjustingTimeOffset = false;
            }

            // Linker Stick: Hue Min/Max
            if (Mathf.Abs(leftStick.x) > 0.1f)
            {
                // X-Achse: Hue Minimum
                m_pinkHSVMin.x = Mathf.Clamp(m_pinkHSVMin.x + leftStick.x * m_hsvAdjustSpeed, 0, 180);
            }
            if (Mathf.Abs(leftStick.y) > 0.1f)
            {
                // Y-Achse: Hue Maximum
                m_pinkHSVMax.x = Mathf.Clamp(m_pinkHSVMax.x + leftStick.y * m_hsvAdjustSpeed, 0, 180);
            }

            // Rechter Stick: Saturation Min/Max (wenn linker Trigger) oder Value Min/Max (wenn rechter Trigger)
            if (leftTrigger)
            {
                // Saturation anpassen
                if (Mathf.Abs(rightStick.x) > 0.1f)
                {
                    // X-Achse: Saturation Minimum
                    m_pinkHSVMin.y = Mathf.Clamp(m_pinkHSVMin.y + rightStick.x * m_hsvAdjustSpeed * 2, 0, 255);
                }
                if (Mathf.Abs(rightStick.y) > 0.1f)
                {
                    // Y-Achse: Saturation Maximum
                    m_pinkHSVMax.y = Mathf.Clamp(m_pinkHSVMax.y + rightStick.y * m_hsvAdjustSpeed * 2, 0, 255);
                }
            }
            else if (rightTrigger)
            {
                // Value anpassen
                if (Mathf.Abs(rightStick.x) > 0.1f)
                {
                    // X-Achse: Value Minimum
                    m_pinkHSVMin.z = Mathf.Clamp(m_pinkHSVMin.z + rightStick.x * m_hsvAdjustSpeed * 2, 0, 255);
                }
                if (Mathf.Abs(rightStick.y) > 0.1f)
                {
                    // Y-Achse: Value Maximum
                    m_pinkHSVMax.z = Mathf.Clamp(m_pinkHSVMax.z + rightStick.y * m_hsvAdjustSpeed * 2, 0, 255);
                }
            }

            // Offset-Kontrolle mit Grip-Button
            if (leftGrip)
            {
                // X/Y Offset mit rechtem Stick
                if (Mathf.Abs(rightStick.x) > 0.1f)
                    m_ballOffset.x += rightStick.x * m_hsvAdjustSpeed;
                if (Mathf.Abs(rightStick.y) > 0.1f)
                    m_ballOffset.y += rightStick.y * m_hsvAdjustSpeed;

                // Z Offset mit Triggern
                if (leftTrigger)
                    m_ballOffset.z += m_hsvAdjustSpeed;
                if (rightTrigger)
                    m_ballOffset.z -= m_hsvAdjustSpeed;
            }

            // Offset-Kontrolle mit beiden Triggern
            if (leftTrigger && rightTrigger)
            {
                // Hole die Position des rechten Controllers
                Vector3 controllerPosition = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);

                if (!m_isSettingOffset)
                {
                    // Starte Offset-Einstellung
                    m_isSettingOffset = true;
                    m_controllerStartPosition = controllerPosition;
                }
                else
                {
                    // Berechne Offset basierend auf Controller-Bewegung
                    m_ballOffset = controllerPosition - m_controllerStartPosition;
                }
            }
            else if (m_isSettingOffset)
            {
                // Beende Offset-Einstellung
                m_isSettingOffset = false;
            }

            // Aktualisiere die Werte im ColorObject
            UpdatePinkHSVValues();
        }

        private void ProcessingThreadFunction()
        {
            while (m_isProcessing)
            {
                if (m_frameQueue.TryDequeue(out RenderTextureData frameData))
                {
                    try
                    {
                        lock (m_matLock)
                        {
                            // Convert byte array to Mat
                            Mat rgbaMat = new Mat(frameData.Height, frameData.Width, CvType.CV_8UC4);
                            rgbaMat.put(0, 0, frameData.TextureBytes);

                            // Convert to RGB and then to HSV
                            Imgproc.cvtColor(rgbaMat, m_rgbMat, Imgproc.COLOR_RGBA2RGB);
                            Imgproc.cvtColor(m_rgbMat, m_hsvMat, Imgproc.COLOR_RGB2HSV);

                            // Find pink objects
                            Core.inRange(m_hsvMat, m_pinkBall.getHSVmin(), m_pinkBall.getHSVmax(), m_thresholdMat);

                            // Morphological operations
                            Mat erodeElement = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
                            Mat dilateElement = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(8, 8));
                            
                            Imgproc.erode(m_thresholdMat, m_thresholdMat, erodeElement);
                            Imgproc.erode(m_thresholdMat, m_thresholdMat, erodeElement);
                            Imgproc.dilate(m_thresholdMat, m_thresholdMat, dilateElement);
                            Imgproc.dilate(m_thresholdMat, m_thresholdMat, dilateElement);

                            // Find contours
                            List<MatOfPoint> contours = new List<MatOfPoint>();
                            Mat hierarchy = new Mat();
                            Imgproc.findContours(m_thresholdMat, contours, hierarchy, 
                                Imgproc.RETR_EXTERNAL, Imgproc.CHAIN_APPROX_SIMPLE);

                            // Process results
                            BallDetectionResult result = new BallDetectionResult { BallFound = false };

                            if (contours.Count > 0)
                            {
                                // Find largest blob
                                double maxArea = 0;
                                Point maxCenter = new Point();
                                double maxRadius = 0;

                                foreach (var contour in contours)
                                {
                                    double area = Imgproc.contourArea(contour);
                                    if (area > 25 / (m_processingDivider * m_processingDivider))
                                    {
                                        Point[] points = contour.toArray();
                                        Point center = new Point();
                                        
                                        foreach (Point p in points)
                                        {
                                            center.x += p.x;
                                            center.y += p.y;
                                        }
                                        center.x /= points.Length;
                                        center.y /= points.Length;
                                        
                                        double currentRadius = 0;
                                        foreach (Point p in points)
                                        {
                                            double dx = p.x - center.x;
                                            double dy = p.y - center.y;
                                            double distance = Math.Sqrt(dx * dx + dy * dy);
                                            currentRadius = Math.Max(currentRadius, distance);
                                        }

                                        if (area > maxArea)
                                        {
                                            maxArea = area;
                                            maxCenter = center;
                                            maxRadius = currentRadius;
                                        }
                                    }
                                    contour.Dispose();
                                }

                                if (maxArea > 0)
                                {
                                    // Calculate 3D position
                                    float originalToProcessedRatio = (float)frameData.CameraIntrinsics.Resolution.x / frameData.Width;
                                    float fx_scaled = frameData.CameraIntrinsics.FocalLength.x / originalToProcessedRatio / m_processingDivider;
                                    float fy_scaled = frameData.CameraIntrinsics.FocalLength.y / originalToProcessedRatio / m_processingDivider;
                                    float cx_scaled = frameData.CameraIntrinsics.PrincipalPoint.x / originalToProcessedRatio / m_processingDivider;
                                    float cy_scaled = frameData.CameraIntrinsics.PrincipalPoint.y / originalToProcessedRatio / m_processingDivider;

                                    // Korrigiere Verzerrung
                                    MatOfPoint2f distortedPoint = new MatOfPoint2f();
                                    Point p = new Point(maxCenter.x, maxCenter.y);
                                    distortedPoint.fromArray(new Point[] { p });
                                    MatOfPoint2f undistortedPoint = new MatOfPoint2f();

                                    // Erstelle Kameramatrix
                                    Mat cameraMatrix = new Mat(3, 3, CvType.CV_64FC1);
                                    cameraMatrix.put(0, 0, new double[] {
                                        fx_scaled, 0, cx_scaled,
                                        0, fy_scaled, cy_scaled,
                                        0, 0, 1
                                    });

                                    // Erstelle Verzerrungskoeffizienten für Quest Pro
                                    // Basierend auf der Brennweite und dem FOV der Kamera
                                    // k1, k2, p1, p2, k3
                                    MatOfDouble distCoeffs = new MatOfDouble(0.15, -0.035, 0.0, 0.0, 0.008);

                                    // Entzerrung des Punktes
                                    Calib3d.undistortPoints(distortedPoint, undistortedPoint, cameraMatrix, distCoeffs);

                                    // Hole entzerrte Koordinaten
                                    Point[] undistortedPoints = undistortedPoint.toArray();
                                    Point undistorted = undistortedPoints[0];

                                    // Berechne normalisierte Koordinaten mit entzerrtem Punkt
                                    float normalizedX = (float)((undistorted.x * fx_scaled + cx_scaled - cx_scaled) / fx_scaled);
                                    float normalizedY = (float)((undistorted.y * fy_scaled + cy_scaled - cy_scaled) / fy_scaled);
                                    
                                    float apparentDiameter = (float)maxRadius * 2.0f;
                                    float distance = (fx_scaled * m_ballDiameterInMeters) / apparentDiameter;

                                    Vector3 pointInCameraSpace = new Vector3(
                                        normalizedX * distance,
                                        normalizedY * distance,
                                        distance
                                    );

                                    result.BallFound = true;
                                    result.WorldPosition = frameData.CameraToWorldMatrix.MultiplyPoint3x4(pointInCameraSpace);
                                    result.Diameter = apparentDiameter;

                                    // Cleanup
                                    distortedPoint.Dispose();
                                    undistortedPoint.Dispose();
                                    cameraMatrix.Dispose();
                                    distCoeffs.Dispose();
                                }

                                // Cleanup
                                hierarchy.Dispose();
                                erodeElement.Dispose();
                                dilateElement.Dispose();
                                rgbaMat.Dispose();
                            }

                            m_resultQueue.Enqueue(result);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"Processing thread error: {e.Message}\n{e.StackTrace}");
                    }
                }
                else
                {
                    Thread.Sleep(1); // Prevent tight loop
                }
            }
        }

        /// <summary>
        /// Stores the current camera pose with current timestamp
        /// </summary>
        private void StoreCameraPose()
        {
            var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
            
            // Get current Unity time in nanoseconds
            long unityTimeNs = (long)(Time.realtimeSinceStartupAsDouble * 1000000000);
            
            // Apply offset to convert Unity time to system time equivalent
            long adjustedTimestamp = unityTimeNs;
            if (m_timeOffsetInitialized)
            {
                adjustedTimestamp = unityTimeNs + m_systemToUnityTimeOffsetNs;
            }

            Debug.Log($"Speichere neue Kamera-Pose: Time={adjustedTimestamp}, Pos={cameraPose.position}");

            // Store in ring buffer
            m_cameraPoseHistoryIndex = (m_cameraPoseHistoryIndex + 1) % m_cameraPoseHistorySize;
            m_cameraPoseHistory[m_cameraPoseHistoryIndex] = new TimestampedCameraPose
            {
                Timestamp = adjustedTimestamp,
                Position = cameraPose.position,
                Rotation = cameraPose.rotation
            };
        }

        /// <summary>
        /// Returns a transform representing the interpolated camera pose at the given timestamp
        /// </summary>
        private Transform GetCameraPoseForTimestamp(long sensorTimestamp)
        {
            long adjustedTimestamp = sensorTimestamp - (long)(m_manualTimeOffsetMs * 1000000);
            
            Debug.Log($"Suche Pose für Timestamp: {adjustedTimestamp}");
            
            // Finde die zwei nächstgelegenen Posen
            int beforeIndex = -1;
            int afterIndex = -1;
            long beforeTimeDiff = long.MaxValue;
            long afterTimeDiff = long.MaxValue;

            // Suche die nächsten Posen vor und nach dem Ziel-Timestamp
            for (int i = 0; i < m_cameraPoseHistorySize; i++)
            {
                if (m_cameraPoseHistory[i].Timestamp == 0) continue;
                
                long timeDiff = m_cameraPoseHistory[i].Timestamp - adjustedTimestamp;
                
                // Log jede geprüfte Pose
                Debug.Log($"Prüfe Index {i}: Timestamp={m_cameraPoseHistory[i].Timestamp}, " +
                         $"TimeDiff={timeDiff}ns ({timeDiff/1000000.0f}ms)");
                
                if (timeDiff <= 0 && -timeDiff < beforeTimeDiff)
                {
                    // Pose liegt vor dem Ziel-Timestamp
                    beforeTimeDiff = -timeDiff;
                    beforeIndex = i;
                    Debug.Log($"Neue beste 'vor' Pose gefunden: Index={i}, TimeDiff={beforeTimeDiff}ns");
                }
                else if (timeDiff > 0 && timeDiff < afterTimeDiff)
                {
                    // Pose liegt nach dem Ziel-Timestamp
                    afterTimeDiff = timeDiff;
                    afterIndex = i;
                    Debug.Log($"Neue beste 'nach' Pose gefunden: Index={i}, TimeDiff={afterTimeDiff}ns");
                }
            }

            // Prüfe ob wir zwei gültige Posen gefunden haben
            if (beforeIndex == -1 || afterIndex == -1)
            {
                Debug.LogWarning($"Keine gültigen Posen gefunden! BeforeIndex={beforeIndex}, AfterIndex={afterIndex}");
                m_skippedFrameCount++;
                return null;
            }

            // Berechne den Interpolationsfaktor (0-1)
            float totalTimeDiff = beforeTimeDiff + afterTimeDiff;
            float t = (float)beforeTimeDiff / totalTimeDiff;
            
            Debug.Log($"Interpoliere zwischen Posen: " +
                      $"\nBefore (Index {beforeIndex}): Time={m_cameraPoseHistory[beforeIndex].Timestamp}, Pos={m_cameraPoseHistory[beforeIndex].Position}" +
                      $"\nAfter (Index {afterIndex}): Time={m_cameraPoseHistory[afterIndex].Timestamp}, Pos={m_cameraPoseHistory[afterIndex].Position}" +
                      $"\nInterpolationsfaktor t={t}");

            // Hole die zwei Posen
            TimestampedCameraPose beforePose = m_cameraPoseHistory[beforeIndex];
            TimestampedCameraPose afterPose = m_cameraPoseHistory[afterIndex];

            // Interpoliere Position und Rotation
            Vector3 interpolatedPosition = Vector3.Lerp(beforePose.Position, afterPose.Position, t);
            Quaternion interpolatedRotation = Quaternion.Slerp(beforePose.Rotation, afterPose.Rotation, t);
            
            Debug.Log($"Interpolierte Position: {interpolatedPosition}");

            // Verwende das existierende temporäre GameObject
            _tempPoseObject.transform.position = interpolatedPosition;
            _tempPoseObject.transform.rotation = interpolatedRotation;

            // Prüfe ob die Zeitdifferenz innerhalb akzeptabler Grenzen liegt
            if (beforeTimeDiff / 1000000.0f > m_currentTimeDifferenceThresholdMs || 
                afterTimeDiff / 1000000.0f > m_currentTimeDifferenceThresholdMs)
            {
                Debug.LogWarning($"Zeitdifferenz zu groß! BeforeTimeDiff={beforeTimeDiff/1000000.0f}ms, " +
                                $"AfterTimeDiff={afterTimeDiff/1000000.0f}ms, " +
                                $"Threshold={m_currentTimeDifferenceThresholdMs}ms");
                m_skippedFrameCount++;
                return null;
            }

            m_processedFrameCount++;
            return _tempPoseObject.transform;
        }

        private void OnDisable()
        {
            // Bestehender Code...
            
            // Visualisierungen entfernen
            ClearMarkerVisualizations();

            // Im OnDisable
            if (m_boardVisualization != null)
            {
                Destroy(m_boardVisualization);
                m_boardVisualization = null;
            }
        }

        // Add a new method to calculate time offset
        private void CalculateTimeOffset(long systemTimeNs)
        {
            long unityTimeNs = (long)(Time.realtimeSinceStartupAsDouble * 1000000000);
            m_systemToUnityTimeOffsetNs = systemTimeNs - unityTimeNs;
            m_timeOffsetInitialized = true;
        }

        // Add method to subscribe to the camera timestamps
        private void SubscribeToFrameTimestamps()
        {
            if (m_cameraPreview != null)
            {
                // Subscribe to the timestamp event
                m_cameraPreview.OnFrameTimestampsUpdated += OnFrameTimestampsUpdated;
            }
        }

        // Handler for frame timestamp updates
        private void OnFrameTimestampsUpdated(SimpleCameraPreview.FrameTimestamps timestamps)
        {
            if (!m_timeOffsetInitialized)
            {
                // Initialize time offset on first frame
                CalculateTimeOffset(timestamps.SystemTimestampNs);
                
                // Share the time offset with the capture session for more accurate delay calculations
                if (m_cameraPreview != null)
                {
                    m_cameraPreview.SetCaptureSessionTimeOffset(m_systemToUnityTimeOffsetNs);
                }
            }
        }

        private void UpdateQualityIndicator(ArUcoMarkerTracking.DetectionQualityClass quality)
        {
            if (m_qualityIndicator == null)
                return;
        
            // Show quality indicator
            m_qualityIndicator.SetActive(m_allGridMarkersDetected);
        
            // Update color based on quality
            MeshRenderer renderer = m_qualityIndicator.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                switch (quality)
                {
                    case ArUcoMarkerTracking.DetectionQualityClass.Excellent:
                        renderer.material = m_excellentQualityMaterial;
                        break;
                    case ArUcoMarkerTracking.DetectionQualityClass.Good:
                        renderer.material = m_goodQualityMaterial;
                        break;
                    case ArUcoMarkerTracking.DetectionQualityClass.Moderate:
                        renderer.material = m_moderateQualityMaterial;
                        break;
                    case ArUcoMarkerTracking.DetectionQualityClass.Poor:
                        renderer.material = m_poorQualityMaterial;
                        break;
                }
            }
        }

        /// <summary>
        /// Collects poses without affecting marker object transforms
        /// </summary>
        private void CollectPosesWithoutAffectingTransforms(Transform cameraTransform)
        {
            // Abrufen der erkannten Marker-IDs
            HashSet<int> detectedMarkers = m_arucoMarkerTracking.GetDetectedMarkerIds();
            
            // Log welche Marker erkannt wurden
            if (detectedMarkers.Count == 0)
            {
                return;
            }
            
            // Erstelle temporäre GameObjects für die Pose-Schätzung
            Dictionary<int, GameObject> tempMarkerObjects = new Dictionary<int, GameObject>();
            
            foreach (int markerId in detectedMarkers)
            {
                GameObject tempObj = new GameObject($"TempMarker_{markerId}");
                tempMarkerObjects[markerId] = tempObj;
            }
            
            // Schätze die Posen für die temporären Objekte
            m_arucoMarkerTracking.EstimatePoseCanonicalMarker(tempMarkerObjects, cameraTransform);
            
            // Sammle die Posen von den temporären Objekten
            foreach (var entry in tempMarkerObjects)
            {
                int markerId = entry.Key;
                GameObject tempObj = entry.Value;
                
                // Sammle nur Posen für Marker, die wir in der ArObjects-Liste haben
                if (m_markerGameObjectDictionary.ContainsKey(markerId))
                {
                    // Erstelle eine neue Pose aus der temporären Transform
                    Pose detectedPose = new Pose(tempObj.transform.position, tempObj.transform.rotation);
                    
                    // Füge die Pose zur Sammlung hinzu
                    if (!m_collectedPoses.ContainsKey(markerId))
                    {
                        m_collectedPoses[markerId] = new List<Pose>();
                    }
                    
                    m_collectedPoses[markerId].Add(detectedPose);
                }
                
                // Temporäres Objekt entfernen
                Destroy(tempObj);
            }
            
            // Überprüfe, ob wir genügend Posen für die Mittelwertbildung haben
            CheckForSufficientPoses();
        }

        /// <summary>
        /// Checks if we have collected enough poses for averaging
        /// </summary>
        private void CheckForSufficientPoses()
        {
            bool hasEnough = true;
            
            // Check if all markers have enough poses
            foreach (int markerId in m_markerGameObjectDictionary.Keys)
            {
                if (!m_collectedPoses.ContainsKey(markerId) || m_collectedPoses[markerId].Count < m_posesToCollect)
                {
                    hasEnough = false;
                    break;
                }
            }
            
            // Only recalculate if we just reached enough poses or if we've lost some
            if (hasEnough != m_hasSufficientPoses)
            {
                m_hasSufficientPoses = hasEnough;
                
                if (m_hasSufficientPoses)
                {
                    CalculateAveragedPoses();
                }
            }
        }

        /// <summary>
        /// Calculates the averaged pose for each marker
        /// </summary>
        private void CalculateAveragedPoses()
        {
            foreach (var entry in m_collectedPoses)
            {
                int markerId = entry.Key;
                List<Pose> poses = entry.Value;
                
                if (poses.Count >= m_posesToCollect)
                {
                    // Average positions
                    Vector3 avgPosition = Vector3.zero;
                    foreach (var pose in poses)
                    {
                        avgPosition += pose.position;
                    }
                    avgPosition /= poses.Count;
                    
                    // Average rotations
                    Quaternion avgRotation = AverageQuaternions(poses.Select(p => p.rotation).ToList());
                    
                    // Store the averaged pose
                    m_averagedPoses[markerId] = new Pose(avgPosition, avgRotation);
                }
            }
            
            // Apply the averaged poses to the marker GameObjects
            ApplyAveragedPoses();
        }

        /// <summary>
        /// Applies the averaged poses to the marker GameObjects
        /// </summary>
        private void ApplyAveragedPoses()
        {
            bool positionChanged = false;
            
            foreach (var entry in m_averagedPoses)
            {
                int markerId = entry.Key;
                Pose newAvgPose = entry.Value;
                
                // Check if we have a previous average to blend with
                if (m_previousAveragedPoses.TryGetValue(markerId, out Pose previousAvgPose))
                {
                    // Blend the new and previous averaged positions
                    Vector3 blendedPosition = Vector3.Lerp(newAvgPose.position, previousAvgPose.position, m_previousAverageWeight);
                    
                    // Nur die Y-Achsen-Rotation extrahieren und mischen
                    float newYRotation = newAvgPose.rotation.eulerAngles.y;
                    float prevYRotation = previousAvgPose.rotation.eulerAngles.y;
                    
                    // Handhabung des 360-Grad-Übergangs (z.B. 350 -> 10 Grad)
                    if (Mathf.Abs(newYRotation - prevYRotation) > 180f)
                    {
                        if (newYRotation > prevYRotation)
                            prevYRotation += 360f;
                        else
                            newYRotation += 360f;
                    }
                    
                    // Interpoliere die Y-Rotation
                    float blendedYRotation = Mathf.Lerp(newYRotation, prevYRotation, m_previousAverageWeight) % 360f;
                    
                    // Erstelle eine neue Rotation, die nur den Y-Wert verändert
                    Quaternion blendedRotation = Quaternion.Euler(0, blendedYRotation, 0);
                    
                    // Create the final blended pose
                    Pose blendedPose = new Pose(blendedPosition, blendedRotation);
                    
                    // Store the blended pose as the new previous for next time
                    m_previousAveragedPoses[markerId] = blendedPose;
                    
                    // Apply the blended pose to the GameObject if it exists in our dictionary
                    if (m_markerGameObjectDictionary.TryGetValue(markerId, out GameObject markerObject))
                    {
                        // Prüfen, ob sich die Position oder Y-Rotation wirklich geändert hat
                        float currentYRotation = markerObject.transform.rotation.eulerAngles.y;
                        float rotationDifference = Mathf.Abs(Mathf.DeltaAngle(currentYRotation, blendedYRotation));
                        
                        if (Vector3.Distance(markerObject.transform.position, blendedPose.position) > 0.0001f ||
                            rotationDifference > 0.1f)
                        {
                            // Neue Position anwenden
                            markerObject.transform.position = blendedPose.position;
                            
                            // Aktuelle X und Z Rotation beibehalten, nur Y ändern
                            Vector3 currentRotation = markerObject.transform.rotation.eulerAngles;
                            markerObject.transform.rotation = Quaternion.Euler(currentRotation.x, blendedYRotation, currentRotation.z);
                            
                            positionChanged = true;
                        }
                    }
                }
                else
                {
                    // No previous average exists yet, just use the new average
                    // Nur Y-Rotation beibehalten
                    Vector3 rotationAngles = newAvgPose.rotation.eulerAngles;
                    Quaternion yOnlyRotation = Quaternion.Euler(0, rotationAngles.y, 0);
                    
                    Pose yRotationOnlyPose = new Pose(newAvgPose.position, yOnlyRotation);
                    m_previousAveragedPoses[markerId] = yRotationOnlyPose;
                    
                    // Apply the pose to the GameObject if it exists in our dictionary
                    if (m_markerGameObjectDictionary.TryGetValue(markerId, out GameObject markerObject))
                    {
                        // Neue Position anwenden
                        markerObject.transform.position = newAvgPose.position;
                        
                        // Aktuelle X und Z Rotation beibehalten, nur Y ändern
                        Vector3 currentRotation = markerObject.transform.rotation.eulerAngles;
                        markerObject.transform.rotation = Quaternion.Euler(currentRotation.x, rotationAngles.y, currentRotation.z);
                        
                        positionChanged = true;
                    }
                }
            }
            
            // Sound abspielen, wenn sich die Position geändert hat
            if (positionChanged && m_audioSource != null && m_positionUpdateSound != null)
            {
                m_audioSource.PlayOneShot(m_positionUpdateSound);
            }
            
            // Visualisierungen aktualisieren
            if (m_showMarkerPositionVisualizations)
            {
                UpdateMarkerVisualizations();
            }
            
            // After applying, reset collection if configured to do so
            if (m_resetCollectionAfterApplying)
            {
                // Clear collected poses to start fresh
                foreach (var key in m_collectedPoses.Keys.ToList())
                {
                    m_collectedPoses[key].Clear();
                }
                
                // Reset sufficient poses flag
                m_hasSufficientPoses = false;
            }

            // Nach dem Anwenden der Posen auch das Board aktualisieren
            UpdateBoardVisualization();
        }

        /// <summary>
        /// Average multiple quaternions
        /// </summary>
        private Quaternion AverageQuaternions(List<Quaternion> quaternions)
        {
            if (quaternions.Count == 0)
                return Quaternion.identity;
            
            if (quaternions.Count == 1)
                return quaternions[0];
            
            // Use a simple averaging approach for just two quaternions
            // This works well for quaternions that are close together
            Quaternion result = quaternions[0];
            
            for (int i = 1; i < quaternions.Count; i++)
            {
                // Handle possible opposite-facing quaternions
                if (Quaternion.Dot(result, quaternions[i]) < 0)
                {
                    result = Quaternion.Slerp(result, Quaternion.Inverse(quaternions[i]), 0.5f);
                }
                else
                {
                    result = Quaternion.Slerp(result, quaternions[i], 0.5f);
                }
            }
            
            return result;
        }

        /// <summary>
        /// Erstellt oder aktualisiert Visualisierungen für Marker-Positionen
        /// </summary>
        private void UpdateMarkerVisualizations()
        {
            foreach (var entry in m_markerGameObjectDictionary)
            {
                int markerId = entry.Key;
                GameObject markerObject = entry.Value;
                
                if (markerObject != null)
                {
                    // Erstelle oder aktualisiere die Marker-Visualisierung
                    if (!m_markerVisualizations.TryGetValue(markerId, out GameObject visualization))
                    {
                        // Erstelle eine neue Sphere für die Marker-Position
                        visualization = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        visualization.name = $"MarkerVisualization_{markerId}";
                        visualization.transform.localScale = new Vector3(0.1f, 0.1f, 0.1f);
                        
                        // Material zuweisen, falls vorhanden
                        if (m_markerVisualizationMaterial != null)
                        {
                            Renderer renderer = visualization.GetComponent<Renderer>();
                            if (renderer != null)
                            {
                                renderer.material = m_markerVisualizationMaterial;
                            }
                        }
                        
                        // Kollisionen deaktivieren
                        Collider collider = visualization.GetComponent<Collider>();
                        if (collider != null)
                        {
                            collider.enabled = false;
                        }
                        
                        m_markerVisualizations[markerId] = visualization;
                    }
                    
                    // Position aktualisieren
                    visualization.transform.position = markerObject.transform.position;
                    visualization.transform.rotation = Quaternion.identity; // Keine Rotation für die Visualisierung
                }
            }
            
            // Visualisierung für die Mitte des Boards hinzufügen
            if (m_averagedPoses.Count > 0)
            {
                // Berechne die Mitte aus allen verfügbaren Marker-Positionen
                Vector3 boardCenter = Vector3.zero;
                int markerCount = 0;
                
                // Summiere alle Marker-Positionen
                foreach (var pose in m_averagedPoses.Values)
                {
                    boardCenter += pose.position;
                    markerCount++;
                }
                
                // Berechne den Durchschnitt
                if (markerCount > 0)
                {
                    boardCenter /= markerCount;
                }
                
                // Erstelle oder aktualisiere die Visualisierung des Zentrums
                if (!m_markerVisualizations.TryGetValue(-1, out GameObject centerVisualization))
                {
                    centerVisualization = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    centerVisualization.name = "BoardCenterVisualization";
                    centerVisualization.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f);
                    
                    // Ein anderes Material verwenden, um das Zentrum zu markieren
                    Renderer renderer = centerVisualization.GetComponent<Renderer>();
                    if (renderer != null)
                    {
                        renderer.material = new Material(Shader.Find("Standard"));
                        renderer.material.color = Color.yellow;
                    }
                    
                    Collider collider = centerVisualization.GetComponent<Collider>();
                    if (collider != null)
                    {
                        collider.enabled = false;
                    }
                    
                    m_markerVisualizations[-1] = centerVisualization;
                }
                
                centerVisualization.transform.position = boardCenter;
            }
            
            // Visualisierungen für die gemittelten Positionen
            foreach (var entry in m_averagedPoses)
            {
                int markerId = entry.Key;
                Pose avgPose = entry.Value;
                
                // Erstelle oder aktualisiere die gemittelte Positions-Visualisierung
                if (!m_averagedPositionVisualizations.TryGetValue(markerId, out GameObject avgVisualization))
                {
                    // Erstelle eine neue Sphere für die gemittelte Position
                    avgVisualization = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    avgVisualization.name = $"AvgPoseVisualization_{markerId}";
                    avgVisualization.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f);
                    
                    // Material zuweisen, falls vorhanden
                    if (m_averagedPositionMaterial != null)
                    {
                        Renderer renderer = avgVisualization.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            renderer.material = m_averagedPositionMaterial;
                        }
                    }
                    else
                    {
                        // Fallback-Material mit verschiedenen Farben je nach Marker-ID
                        Renderer renderer = avgVisualization.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            renderer.material = new Material(Shader.Find("Standard"));
                            
                            // Verschiedene Farben je nach Marker-ID
                            switch(markerId)
                            {
                                case 0: renderer.material.color = Color.red; break;
                                case 1: renderer.material.color = Color.green; break;
                                case 2: renderer.material.color = Color.blue; break;
                                case 3: renderer.material.color = Color.cyan; break;
                                default: renderer.material.color = Color.magenta; break;
                            }
                        }
                    }
                    
                    // Kollisionen deaktivieren
                    Collider collider = avgVisualization.GetComponent<Collider>();
                    if (collider != null)
                    {
                        collider.enabled = false;
                    }
                    
                    m_averagedPositionVisualizations[markerId] = avgVisualization;
                }
                
                // Position aktualisieren
                avgVisualization.transform.position = avgPose.position;
                avgVisualization.transform.rotation = Quaternion.identity; // Keine Rotation für die Visualisierung
            }
        }

        /// <summary>
        /// Entfernt Parent-Beziehungen von Marker-Objekten, um unerwünschte Bewegungen zu vermeiden
        /// </summary>
        private void UnparentMarkerObjects()
        {
            foreach (var entry in m_markerGameObjectDictionary)
            {
                GameObject markerObject = entry.Value;
                if (markerObject != null && markerObject.transform.parent != null)
                {
                    markerObject.transform.parent = null; // Root-Level in der Hierarchie
                }
            }
        }

        /// <summary>
        /// Deaktiviert physikalische Komponenten auf Marker-Objekten
        /// </summary>
        private void DisablePhysicsOnMarkerObjects()
        {
            foreach (var entry in m_markerGameObjectDictionary)
            {
                GameObject markerObject = entry.Value;
                if (markerObject != null)
                {
                    // Rigidbody deaktivieren/einfrieren, falls vorhanden
                    Rigidbody rb = markerObject.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.detectCollisions = false;
                    }
                    
                    // Alle Collider deaktivieren
                    Collider[] colliders = markerObject.GetComponentsInChildren<Collider>();
                    foreach (var collider in colliders)
                    {
                        collider.enabled = false;
                    }
                }
            }
        }

        /// <summary>
        /// Entfernt alle Marker-Visualisierungen
        /// </summary>
        private void ClearMarkerVisualizations()
        {
            // Lösche Marker-Visualisierungen
            foreach (var visualization in m_markerVisualizations.Values)
            {
                if (visualization != null)
                {
                    Destroy(visualization);
                }
            }
            m_markerVisualizations.Clear();
            
            // Lösche gemittelte Positions-Visualisierungen
            foreach (var visualization in m_averagedPositionVisualizations.Values)
            {
                if (visualization != null)
                {
                    Destroy(visualization);
                }
            }
            m_averagedPositionVisualizations.Clear();
        }

        /// <summary>
        /// Zeigt Informationen über alle erkannten Marker an
        /// </summary>
        private void LogAllDetectedMarkers()
        {
            if (m_arucoMarkerTracking == null) return;
            
            HashSet<int> detectedMarkerIds = m_arucoMarkerTracking.GetDetectedMarkerIds();
            Dictionary<int, ArUcoMarkerTracking.MarkerQualityMetrics> qualityMetrics = 
                m_arucoMarkerTracking.GetDetectionQualityMetrics();
            
            // Liste aller erkannten Marker-IDs
            string markersStr = string.Join(", ", detectedMarkerIds);
            
            // Details zu jedem Marker
            foreach (int markerId in detectedMarkerIds)
            {
                if (qualityMetrics.TryGetValue(markerId, out var metrics))
                {
                }
            }
        }

        /// <summary>
        /// Berechnet die gemittelte Position und Rotation für das gesamte Board
        /// </summary>
        private void UpdateBoardVisualization()
        {
            if (!m_showBoardVisualization || m_boardPrefab == null || m_averagedPoses.Count == 0)
                return;
            
            // Erstelle das Board-Objekt, falls es noch nicht existiert
            if (m_boardVisualization == null)
            {
                m_boardVisualization = Instantiate(m_boardPrefab);
                m_boardVisualization.name = "BoardVisualization";
            }
            
            // Berechne den Mittelpunkt aller Marker
            Vector3 centerPosition = Vector3.zero;
            int count = 0;
            
            foreach (var pose in m_averagedPoses.Values)
            {
                centerPosition += pose.position;
                count++;
            }
            
            if (count > 0)
            {
                centerPosition /= count;
            }
            
            // Berechne die Rotation aus der Richtung Marker 0 -> Marker 2
            Quaternion boardRotation = Quaternion.identity;
            
            if (m_averagedPoses.ContainsKey(0) && m_averagedPoses.ContainsKey(2))
            {
                Vector3 marker0Position = m_averagedPoses[0].position;
                Vector3 marker2Position = m_averagedPoses[2].position;
                
                // Berechne Richtungsvektor von Marker 0 zu Marker 2
                Vector3 direction = marker2Position - marker0Position;
                
                // Entferne Y-Komponente für eine horizontale Ausrichtung
                direction.y = 0; 
                
                // Nur fortfahren, wenn die Marker einen signifikanten Abstand haben
                if (direction.magnitude > 0.001f)
                {
                    // Normalisiere den Vektor 
                    direction.Normalize();
                    
                    // Berechne LookAt-Rotation
                    boardRotation = Quaternion.LookRotation(direction, Vector3.up);
                    
                    // WICHTIG: Extrahiere nur die Y-Komponente für eine stabile Rotation
                    float yRotation = boardRotation.eulerAngles.y;
                    boardRotation = Quaternion.Euler(0, yRotation, 0);
                }
            }
            
            // Board-Position und -Rotation aktualisieren
            m_boardVisualization.transform.position = centerPosition;
            m_boardVisualization.transform.rotation = boardRotation;
        }

        /// <summary>
        /// Friert alle Marker-Objekte an ihrer aktuellen Position ein
        /// </summary>
        private void FreezeMarkerObjects()
        {
            foreach (var entry in m_markerGameObjectDictionary)
            {
                GameObject markerObject = entry.Value;
                if (markerObject == null) continue;
                
                // Sicherstellen, dass das Objekt keine Parent-Transformation erbt
                if (markerObject.transform.parent != null)
                {
                    markerObject.transform.SetParent(null, true);
                }
                
                // Alle Physik deaktivieren
                Rigidbody rb = markerObject.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.detectCollisions = false;
                }
                
                // Alle Animatoren stoppen
                Animator animator = markerObject.GetComponent<Animator>();
                if (animator != null)
                {
                    animator.enabled = false;
                }
                
                // Alle Scripts finden und deaktivieren, die Transform ändern könnten (außer unserem)
                MonoBehaviour[] scripts = markerObject.GetComponents<MonoBehaviour>();
                foreach (MonoBehaviour script in scripts)
                {
                    // Unser eigenes Script nicht deaktivieren
                    if (script != this && script.GetType() != typeof(ArUcoTrackingAppCoordinator))
                    {
                        script.enabled = false;
                    }
                }
            }
            
            // Auch das Board-Objekt einfrieren, falls vorhanden
            if (m_boardVisualization != null)
            {
                if (m_boardVisualization.transform.parent != null)
                {
                    m_boardVisualization.transform.SetParent(null, true);
                }
                
                // Physik auf dem Board deaktivieren
                Rigidbody boardRb = m_boardVisualization.GetComponent<Rigidbody>();
                if (boardRb != null)
                {
                    boardRb.isKinematic = true;
                    boardRb.detectCollisions = false;
                }
            }
        }

        private void OnDestroy()
        {
            if (_tempPoseObject != null)
            {
                Destroy(_tempPoseObject);
            }
        }
    }
}


