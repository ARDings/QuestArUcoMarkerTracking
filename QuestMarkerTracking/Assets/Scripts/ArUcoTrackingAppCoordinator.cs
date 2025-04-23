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
        [SerializeField] private int m_processingDivider = 2;

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
                Mat rgbaMat = new Mat(renderTexture.height, renderTexture.width, CvType.CV_8UC4);
                
                RenderTexture.active = renderTexture;
                Texture2D tempTex = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
                tempTex.ReadPixels(new UnityEngine.Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                tempTex.Apply();
                Utils.texture2DToMat(tempTex, rgbaMat);
                Destroy(tempTex);

                // Konvertiere zu RGB und dann zu HSV
                Imgproc.cvtColor(rgbaMat, m_rgbMat, Imgproc.COLOR_RGBA2RGB);
                Imgproc.cvtColor(m_rgbMat, m_hsvMat, Imgproc.COLOR_RGB2HSV);

                // Finde pinke Objekte
                Core.inRange(m_hsvMat, m_pinkBall.getHSVmin(), m_pinkBall.getHSVmax(), m_thresholdMat);

                // Morphologische Operationen
                Mat erodeElement = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
                Mat dilateElement = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(8, 8));
                
                Imgproc.erode(m_thresholdMat, m_thresholdMat, erodeElement);
                Imgproc.erode(m_thresholdMat, m_thresholdMat, erodeElement);
                Imgproc.dilate(m_thresholdMat, m_thresholdMat, dilateElement);
                Imgproc.dilate(m_thresholdMat, m_thresholdMat, dilateElement);

                // Finde Konturen
                List<MatOfPoint> contours = new List<MatOfPoint>();
                Mat hierarchy = new Mat();
                Imgproc.findContours(m_thresholdMat, contours, hierarchy, 
                    Imgproc.RETR_EXTERNAL, Imgproc.CHAIN_APPROX_SIMPLE);

                double maxArea = 0;
                Point maxCenter = new Point();
                double maxRadius = 0;

                // Finde den größten kreisförmigen Blob
                foreach (var contour in contours)
                {
                    double area = Imgproc.contourArea(contour);
                    if (area > 100) // Minimale Fläche für Rauschunterdrückung
                    {
                        Point[] points = contour.toArray();
                        Point center = new Point();
                        
                        // Berechne den Mittelpunkt als Durchschnitt aller Konturpunkte
                        foreach (Point p in points)
                        {
                            center.x += p.x;
                            center.y += p.y;
                        }
                        center.x /= points.Length;
                        center.y /= points.Length;
                        
                        // Berechne den Radius als maximalen Abstand vom Mittelpunkt
                        double currentRadius = 0;
                        foreach (Point p in points)
                        {
                            double dx = p.x - center.x;
                            double dy = p.y - center.y;
                            double distance = Math.Sqrt(dx * dx + dy * dy);
                            currentRadius = Math.Max(currentRadius, distance);
                        }

                        // Wenn dies der bisher größte gefundene Kreis ist
                        if (area > maxArea)
                        {
                            maxArea = area;
                            maxCenter = center;
                            maxRadius = currentRadius;
                        }
                    }
                }

                // Wenn ein Ball gefunden wurde
                if (maxArea > 0)
                {
                    if (m_ballVisualization != null)
                    {
                        // WICHTIG: Hole die ORIGINALEN Kamera-Parameter, nicht die skalierten
                        var cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
                        float fx = cameraIntrinsics.FocalLength.x;  // Originale Focal Length
                        float fy = cameraIntrinsics.FocalLength.y;  // Originale Focal Length
                        float cx = cameraIntrinsics.PrincipalPoint.x;  // Originaler Principal Point
                        float cy = cameraIntrinsics.PrincipalPoint.y;  // Originaler Principal Point

                        // Skaliere die gefundenen Koordinaten HOCH zur originalen Auflösung
                        float scaledX = (float)maxCenter.x * m_processingDivider;
                        float scaledY = (float)maxCenter.y * m_processingDivider;

                        // Berechne normalisierte Koordinaten mit originalen Parametern
                        float normalizedX = (float)((scaledX - cx) / fx);
                        float normalizedY = (float)(-1 * (scaledY - cy) / fy);

                        // Skaliere auch den Radius hoch
                        float scaledRadius = (float)maxRadius * m_processingDivider;
                        float apparentDiameter = scaledRadius * 2.0f;
                        float distance = (fx * m_ballDiameterInMeters) / apparentDiameter;

                        // WICHTIG: Diese Zeile ist der Schlüssel - hole die komplette Kamera-Pose
                        var cameraPose = PassthroughCameraUtils.GetCameraPoseInWorld(CameraEye);
                        
                        // Vektor im lokalen Kamera-Koordinatensystem
                        Vector3 pointInCameraSpace = new Vector3(
                            normalizedX * distance,
                            normalizedY * distance,
                            distance
                        );
                        
                        // Matrix für Kamera-zu-Welt Transformation erstellen
                        Matrix4x4 cameraToWorldMatrix = Matrix4x4.TRS(
                            cameraPose.position,
                            cameraPose.rotation,
                            Vector3.one
                        );
                        
                        // Transformiere Punkt mit der Matrix
                        Vector3 worldPosition = cameraToWorldMatrix.MultiplyPoint3x4(pointInCameraSpace);
                        
                        // Offset anwenden
                        worldPosition += m_ballOffset;
                        
                        // Position smoothen
                        Vector3 currentPos = m_ballVisualization.transform.position;
                        float smoothFactor = 0.5f;
                        Vector3 finalPosition = Vector3.Lerp(currentPos, worldPosition, 1 - smoothFactor);
                        
                        // Anwenden
                        m_ballVisualization.transform.position = finalPosition;
                        m_ballVisualization.transform.localScale = Vector3.one * m_ballDiameterInMeters * m_visualScaleFactor;
                    }
                }

                // Aufräumen
                rgbaMat.Dispose();
                hierarchy.Dispose();
                foreach (var contour in contours)
                    contour.Dispose();
                erodeElement.Dispose();
                dilateElement.Dispose();

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

        private void OnDestroy()
        {
            if (m_rgbMat != null) m_rgbMat.Dispose();
            if (m_hsvMat != null) m_hsvMat.Dispose();
            if (m_thresholdMat != null) m_thresholdMat.Dispose();
        }
    }
}
