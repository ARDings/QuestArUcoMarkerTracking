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
        }

        /// <summary>
        /// Updates camera poses, detects markers, and handles input for toggling visualization mode.
        /// </summary>
        private void Update()
        {
            if (!m_cameraPreview.AreCamerasReady) return;

            UpdateCameraPoses();

            if (m_enableMarkerTracking && m_arucoMarkerTracking.IsReady)
            {
                m_arucoMarkerTracking.DetectMarker(m_cameraPreview.LeftCameraTexture);
                
                if (m_markerGameObjectDictionary.Count > 0)
                {
                    m_arucoMarkerTracking.EstimatePoseCanonicalMarker(
                        m_markerGameObjectDictionary,
                        m_cameraAnchor
                    );
                }
            }

            if (m_enableColorTracking)
            {
                ProcessBallTracking(m_cameraPreview.LeftCameraTexture);
            }

            UpdateHSVControls();
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
            
            // Debug-Ausgabe aller verfügbaren Kamera-Eigenschaften
            Debug.Log($"[Camera Debug] Camera Eye: {CameraEye}");
            Debug.Log($"[Camera Debug] Resolution: {nativeIntrinsics.Resolution}");
            Debug.Log($"[Camera Debug] Principal Point: {nativeIntrinsics.PrincipalPoint}");
            Debug.Log($"[Camera Debug] Focal Length: {nativeIntrinsics.FocalLength}");
            
            // Versuche alle öffentlichen Properties der Intrinsics auszulesen
            var type = nativeIntrinsics.GetType();
            var properties = type.GetProperties();
            Debug.Log("[Camera Debug] All available properties:");
            foreach (var prop in properties)
            {
                try
                {
                    var value = prop.GetValue(nativeIntrinsics);
                    Debug.Log($"[Camera Debug] {prop.Name}: {value}");
                }
                catch (System.Exception e)
                {
                    Debug.Log($"[Camera Debug] Could not read {prop.Name}: {e.Message}");
                }
            }
            
            // Versuche auch private Felder zu lesen
            var fields = type.GetFields(System.Reflection.BindingFlags.NonPublic | 
                                       System.Reflection.BindingFlags.Instance | 
                                       System.Reflection.BindingFlags.Public);
            Debug.Log("[Camera Debug] All available fields:");
            foreach (var field in fields)
            {
                try
                {
                    var value = field.GetValue(nativeIntrinsics);
                    Debug.Log($"[Camera Debug] {field.Name}: {value}");
                }
                catch (System.Exception e)
                {
                    Debug.Log($"[Camera Debug] Could not read {field.Name}: {e.Message}");
                }
            }

            // Rest des ursprünglichen Codes...
            var cx = nativeIntrinsics.PrincipalPoint.x / m_processingDivider;
            var cy = nativeIntrinsics.PrincipalPoint.y / m_processingDivider;
            var fx = nativeIntrinsics.FocalLength.x / m_processingDivider;
            var fy = nativeIntrinsics.FocalLength.y / m_processingDivider;
            var width = nativeIntrinsics.Resolution.x / m_processingDivider;
            var height = nativeIntrinsics.Resolution.y / m_processingDivider;
            
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
                Debug.LogError($"BallTracking Error: {e.Message}\n{e.StackTrace}");
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

                if (m_showHSVDebug)
                    Debug.Log($"Ball Offset: {m_ballOffset}");
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
                    if (m_showOffsetDebug)
                        Debug.Log("Started offset adjustment");
                }
                else
                {
                    // Berechne Offset basierend auf Controller-Bewegung
                    m_ballOffset = controllerPosition - m_controllerStartPosition;
                    
                    if (m_showOffsetDebug)
                        Debug.Log($"Ball Offset: {m_ballOffset}");
                }
            }
            else if (m_isSettingOffset)
            {
                // Beende Offset-Einstellung
                m_isSettingOffset = false;
                if (m_showOffsetDebug)
                    Debug.Log("Finished offset adjustment");
            }

            // Aktualisiere die Werte im ColorObject
            UpdatePinkHSVValues();

            // Debug-Ausgabe der aktuellen Werte
            if (m_showHSVDebug)
            {
                Debug.Log($"HSV Min: H({m_pinkHSVMin.x:F1}) S({m_pinkHSVMin.y:F1}) V({m_pinkHSVMin.z:F1})");
                Debug.Log($"HSV Max: H({m_pinkHSVMax.x:F1}) S({m_pinkHSVMax.y:F1}) V({m_pinkHSVMax.z:F1})");
            }
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

        private void OnDestroy()
        {
            // Stop processing thread
            m_isProcessing = false;
            m_processingThread?.Join();
            
            if (m_rgbMat != null) m_rgbMat.Dispose();
            if (m_hsvMat != null) m_hsvMat.Dispose();
            if (m_thresholdMat != null) m_thresholdMat.Dispose();
        }
    }
}
