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

        [Header("Camera Texture View")]
        [SerializeField] private WebCamTextureManager m_webCamTextureManager;
        private PassthroughCameraEye CameraEye => m_webCamTextureManager.Eye;
        private Vector2Int CameraResolution => m_webCamTextureManager.RequestedResolution;
        [SerializeField] private Transform m_cameraAnchor;
   
        [SerializeField] private Canvas m_cameraCanvas;
        [SerializeField] private RawImage m_resultRawImage;
        [SerializeField] private float m_canvasDistance = 1f;

        [Header("Marker Tracking")]
        [SerializeField] private ArUcoMarkerTracking m_arucoMarkerTracking;
        [SerializeField, Tooltip("List of marker IDs mapped to their corresponding GameObjects")]
        private List<MarkerGameObjectPair> m_markerGameObjectPairs = new List<MarkerGameObjectPair>();
        private Dictionary<int, GameObject> m_markerGameObjectDictionary = new Dictionary<int, GameObject>();
        private bool m_showCameraCanvas = true;

        private Texture2D m_resultTexture;        // Für Marker-Tracking
        private Texture2D m_colorDebugTexture;    // Für Ball-Tracking

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

        /// <summary>
        /// Initializes the camera, permissions, and marker tracking system.
        /// </summary>
        private IEnumerator Start()
        {
            // Validate required components
            if (m_webCamTextureManager == null)
            {
                Debug.LogError($"PCA: {nameof(m_webCamTextureManager)} field is required " +
                            $"for the component {nameof(ArUcoTrackingAppCoordinator)} to operate properly");
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
            
            // Initialisiere den Pink Ball mit den eingestellten HSV-Werten
            m_pinkBall = new ColorObject("pink");
            UpdatePinkHSVValues();
            InitializeBallTracking();
            
            // Set initial visibility states
            m_cameraCanvas.gameObject.SetActive(m_showCameraCanvas);
            SetMarkerObjectsVisibility(!m_showCameraCanvas);
            if (m_ballVisualization != null)
            {
                m_ballVisualization.SetActive(!m_showCameraCanvas);
            }
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
            if (m_webCamTextureManager.WebCamTexture == null)
                return;

            // Toggle zwischen Kamera-Ansicht und AR-Visualisierung
            HandleVisualizationToggle();
            UpdateCameraPoses();

            // Verarbeite aktive Tracking-Modi
            if (m_enableMarkerTracking && m_arucoMarkerTracking.IsReady)
            {
                ProcessMarkerTracking();
               // HandleObjectSpawningAndDeletion();
            }

            if (m_enableColorTracking)
            {
                ProcessBallTracking(m_webCamTextureManager.WebCamTexture);
            }

            // HSV-Werte mit Controller anpassen
            UpdateHSVControls();
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
                
                // Zeige/Verstecke je nach Modus
                if (m_enableMarkerTracking)
                {
                SetMarkerObjectsVisibility(!m_showCameraCanvas);
                }
                if (m_enableColorTracking && m_ballVisualization != null)
                {
                    m_ballVisualization.SetActive(!m_showCameraCanvas);
                }
            }
        }

        /// <summary>
        /// Performs marker detection and pose estimation.
        /// This is the core functionality that processes camera frames to detect markers
        /// and position virtual objects in 3D space.
        /// </summary>
        private void ProcessMarkerTracking()
        {
            // Step 1: Detect ArUco markers in the current camera frame
            m_arucoMarkerTracking.DetectMarker(m_webCamTextureManager.WebCamTexture, m_resultTexture);
            
            // Step 2: Estimate the pose of markers and position 3D objects accordingly
            // This maps the 2D marker positions to 3D space using the camera parameters
            m_arucoMarkerTracking.EstimatePoseCanonicalMarker(m_markerGameObjectDictionary, m_cameraAnchor);
        }

        /// <summary>
        /// Toggles the visibility of all marker-associated GameObjects in the dictionary.
        /// </summary>
        /// <param name="isVisible">Whether the marker objects should be visible or not.</param>
        private void SetMarkerObjectsVisibility(bool isVisible)
        {
            // Toggle visibility for all GameObjects in the marker dictionary
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
            m_arucoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);
            
            // Step 2: Build marker dictionary from serialized list
            // This maps marker IDs to the GameObjects that should be positioned at each marker
            BuildMarkerDictionary();
            
            // Step 3: Set up texture for visualization
            ConfigureResultTexture(width, height);
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
        /// Configures the texture for displaying camera and tracking results.
        /// </summary>
        /// <param name="width">Width of the camera resolution</param>
        /// <param name="height">Height of the camera resolution</param>
        private void ConfigureResultTexture(int width, int height)
        {
            int divideNumber = m_arucoMarkerTracking.DivideNumber;
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

        private void ProcessBallTracking(WebCamTexture webCamTexture)
        {
            try
            {
                Debug.Log("BallTracking: Starting frame processing...");
                Debug.Log($"BallTracking: Camera resolution: {webCamTexture.width}x{webCamTexture.height}");

                // Konvertiere WebCamTexture zu Mat
                Mat rgbaMat = new Mat(webCamTexture.height, webCamTexture.width, CvType.CV_8UC4);
                Utils.webCamTextureToMat(webCamTexture, rgbaMat);
                Debug.Log($"BallTracking: Converted to Mat: {rgbaMat.width()}x{rgbaMat.height()}");

                // Konvertiere zu RGB und dann zu HSV
                Imgproc.cvtColor(rgbaMat, m_rgbMat, Imgproc.COLOR_RGBA2RGB);
                Imgproc.cvtColor(m_rgbMat, m_hsvMat, Imgproc.COLOR_RGB2HSV);
                Debug.Log("BallTracking: Converted to HSV color space");

                // Finde pinke Objekte
                Core.inRange(m_hsvMat, m_pinkBall.getHSVmin(), m_pinkBall.getHSVmax(), m_thresholdMat);
                Debug.Log($"BallTracking: HSV Range - Min: {m_pinkBall.getHSVmin().val[0]},{m_pinkBall.getHSVmin().val[1]},{m_pinkBall.getHSVmin().val[2]} " +
                          $"Max: {m_pinkBall.getHSVmax().val[0]},{m_pinkBall.getHSVmax().val[1]},{m_pinkBall.getHSVmax().val[2]}");

                // Morphologische Operationen
                Mat erodeElement = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(3, 3));
                Mat dilateElement = Imgproc.getStructuringElement(Imgproc.MORPH_RECT, new Size(8, 8));
                
                Imgproc.erode(m_thresholdMat, m_thresholdMat, erodeElement);
                Imgproc.erode(m_thresholdMat, m_thresholdMat, erodeElement);
                Imgproc.dilate(m_thresholdMat, m_thresholdMat, dilateElement);
                Imgproc.dilate(m_thresholdMat, m_thresholdMat, dilateElement);
                Debug.Log("BallTracking: Applied morphological operations");

                // Finde Konturen
                List<MatOfPoint> contours = new List<MatOfPoint>();
                Mat hierarchy = new Mat();
                Imgproc.findContours(m_thresholdMat, contours, hierarchy, 
                    Imgproc.RETR_EXTERNAL, Imgproc.CHAIN_APPROX_SIMPLE);
                Debug.Log($"BallTracking: Found {contours.Count} contours");

                // Zeige das Ergebnis im Debug-View
                if (m_showColorDebugView && m_resultRawImage != null && m_resultRawImage.enabled)
                {
                    // Zeige das Schwellenwertbild
                    if (m_colorDebugTexture == null || 
                        m_colorDebugTexture.width != m_thresholdMat.width() || 
                        m_colorDebugTexture.height != m_thresholdMat.height())
                    {
                        if (m_colorDebugTexture != null)
                            Destroy(m_colorDebugTexture);
                        m_colorDebugTexture = new Texture2D(m_thresholdMat.width(), m_thresholdMat.height(), 
                            TextureFormat.RGBA32, false);
                    }

                    // Konvertiere das Schwellenwertbild zu RGBA für die Anzeige
                    Mat debugMat = new Mat();
                    Imgproc.cvtColor(m_thresholdMat, debugMat, Imgproc.COLOR_GRAY2RGBA);
                    
                    // Zeichne die Konturen
                    foreach (var contour in contours)
                    {
                        Imgproc.drawContours(debugMat, contours, -1, new Scalar(0, 255, 0, 255), 2);
                    }

                    Utils.matToTexture2D(debugMat, m_colorDebugTexture);
                    m_resultRawImage.texture = m_colorDebugTexture;  // Verwende die separate Debug-Texture
                    debugMat.Dispose();
                }

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

                        Debug.Log($"BallTracking: Found potential ball - Area: {area}, Radius: {currentRadius}");

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
                    Debug.Log($"BallTracking: Detected ball at ({maxCenter.x}, {maxCenter.y}) with radius {maxRadius}");

                    if (m_ballVisualization != null)
                    {
                        // Kamera-Intrinsics wie zuvor
                        var cameraIntrinsics = PassthroughCameraUtils.GetCameraIntrinsics(CameraEye);
                        float fx = cameraIntrinsics.FocalLength.x;
                        float fy = cameraIntrinsics.FocalLength.y;
                        float cx = cameraIntrinsics.PrincipalPoint.x;
                        float cy = cameraIntrinsics.PrincipalPoint.y;

                        // Normalisierte Bildkoordinaten
                        float normalizedX = (float)((maxCenter.x - cx) / fx);
                        float normalizedY = (float)(-1 * (maxCenter.y - cy) / fy); // Y-Achse invertieren
                        
                        // Berechne die Entfernung
                        float apparentDiameter = (float)(maxRadius * 2.0);
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
                else
                {
                    Debug.Log("BallTracking: No ball detected in this frame");
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
            // Cleanup für Ball-Tracking Ressourcen
            if (m_rgbMat != null) m_rgbMat.Dispose();
            if (m_hsvMat != null) m_hsvMat.Dispose();
            if (m_thresholdMat != null) m_thresholdMat.Dispose();
            
            // Cleanup für Texturen
            if (m_resultTexture != null)
                Destroy(m_resultTexture);
            if (m_colorDebugTexture != null)
                Destroy(m_colorDebugTexture);
        }
    }
}
