package com.tryar.camera2

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.ImageFormat
import android.hardware.camera2.*
import android.media.Image
import android.media.ImageReader
import android.os.Handler
import android.os.HandlerThread
import android.util.Log
import android.util.Range
import android.view.Surface
import androidx.core.app.ActivityCompat
import org.json.JSONArray
import org.json.JSONObject
import java.util.concurrent.Semaphore
import java.util.concurrent.TimeUnit
import kotlin.IllegalStateException
import kotlin.math.max
import kotlin.math.min

class Camera2Helper(private val context: Context) {
    companion object {
        private const val TAG = "Camera2Helper"
        private const val CAMERA_TIMEOUT_MS = 2500L
        private const val HEADSET_CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA"
        private const val MAX_IMAGES = 4  // Erhöht für mehr Puffer
    }

    private val cameraManager: CameraManager = context.getSystemService(Context.CAMERA_SERVICE) as CameraManager
    private var cameraDevice: CameraDevice? = null
    private var captureSession: CameraCaptureSession? = null
    private var imageReader: ImageReader? = null
    private var backgroundHandler: Handler? = null
    private var backgroundThread: HandlerThread? = null
    private val cameraOpenCloseLock = Semaphore(1)
    private var imageCallback: ImageCallback? = null
    private var frameWidth = 1280
    private var frameHeight = 720
    private var currentCameraId: String? = null
    private var lastFrameTime = 0L
    private var isInitialized = false

    interface ImageCallback {
        fun onImageAvailable(data: ByteArray, width: Int, height: Int)
    }

    // Starte den Hintergrund-Thread für die Kamera
    private fun startBackgroundThread() {
        try {
            Log.d(TAG, "[Camera2Helper] Starting background thread")
            backgroundThread = HandlerThread("CameraBackground").apply {
                start()
                backgroundHandler = Handler(looper)
            }
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to start background thread: ${e.message}", e)
        }
    }

    // Stoppe den Hintergrund-Thread
    private fun stopBackgroundThread() {
        try {
            Log.d(TAG, "[Camera2Helper] Stopping background thread")
            backgroundThread?.quitSafely()
            try {
                backgroundThread?.join()
                backgroundThread = null
                backgroundHandler = null
            } catch (e: InterruptedException) {
                Log.e(TAG, "[Camera2Helper] Error stopping background thread", e)
            }
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error in stopBackgroundThread: ${e.message}", e)
        }
    }

    // Callback für Kamera-Geräte-Status
    private val cameraStateCallback = object : CameraDevice.StateCallback() {
        override fun onOpened(camera: CameraDevice) {
            cameraOpenCloseLock.release()
            cameraDevice = camera
            Log.d(TAG, "[Camera2Helper] Camera opened successfully: ${camera.id}")
            createCaptureSession()
        }

        override fun onDisconnected(camera: CameraDevice) {
            cameraOpenCloseLock.release()
            Log.e(TAG, "[Camera2Helper] Camera ${camera.id} disconnected!")
            camera.close()
            cameraDevice = null
        }

        override fun onError(camera: CameraDevice, error: Int) {
            cameraOpenCloseLock.release()
            Log.e(TAG, "[Camera2Helper] Camera ${camera.id} error: $error")
            camera.close()
            cameraDevice = null
        }
    }

    // Callback für Capture-Session-Status
    private val captureSessionStateCallback = object : CameraCaptureSession.StateCallback() {
        override fun onConfigured(session: CameraCaptureSession) {
            Log.d(TAG, "[Camera2Helper] CaptureSession configured for camera: ${session.device.id}")
            captureSession = session
            try {
                // Erstelle den Capture Request
                val captureRequest = createCaptureRequest(imageReader!!.surface)
                session.setRepeatingRequest(captureRequest, captureCallback, backgroundHandler)
                Log.d(TAG, "[Camera2Helper] Started repeating capture request for camera: ${session.device.id}")
            } catch (e: Exception) {
                Log.e(TAG, "[Camera2Helper] Failed to start capture: ${e.message}")
            }
        }

        override fun onConfigureFailed(session: CameraCaptureSession) {
            Log.e(TAG, "[Camera2Helper] Failed to configure capture session for camera: ${session.device.id}")
        }
    }

    // Callback für Capture-Ergebnisse
    private val captureCallback = object : CameraCaptureSession.CaptureCallback() {
        override fun onCaptureCompleted(
            session: CameraCaptureSession,
            request: CaptureRequest,
            result: TotalCaptureResult
        ) {
            super.onCaptureCompleted(session, request, result)
            // Hier könnten wir Metadaten aus dem Capture-Ergebnis verarbeiten
        }
    }

    // Listener für neue Bilder
    private val imageReaderListener = ImageReader.OnImageAvailableListener { reader ->
        var image: Image? = null
        try {
            image = reader.acquireLatestImage()
            if (image != null) {
                Log.d(TAG, """[Camera2Helper] Frame Details:
                    |Format: ${image.format}
                    |Size: ${image.width}x${image.height}
                    |Timestamp: ${image.timestamp}
                    |Planes: ${image.planes.size}""".trimMargin())
                
                image.planes.forEachIndexed { index, plane ->
                    Log.d(TAG, """[Camera2Helper] Plane $index Details:
                        |Buffer size: ${plane.buffer.remaining()}
                        |Pixel stride: ${plane.pixelStride}
                        |Row stride: ${plane.rowStride}
                        |Buffer direct: ${plane.buffer.isDirect}
                        |Buffer capacity: ${plane.buffer.capacity()}""".trimMargin())
                }

                val now = System.currentTimeMillis()
                if (lastFrameTime > 0) {
                    Log.d(TAG, "[Camera2Helper] Frame received! Time since last frame: ${now - lastFrameTime}ms")
                } else {
                    Log.d(TAG, "[Camera2Helper] First frame received!")
                }
                lastFrameTime = now

                val width = image.width
                val height = image.height
                
                // Konvertiere das YUV-Bild zu NV21 (ein Format, das leichter zu verarbeiten ist)
                val nv21 = YUV_420_888toNV21(image)
                
                // Analysiere die Pixeldaten für Debugging
                analyzePixelData(nv21, width, height)
                
                // Sende die Daten an den Callback
                imageCallback?.onImageAvailable(nv21, width, height)
            } else {
                Log.w(TAG, "[Camera2Helper] Received null image from camera $currentCameraId")
            }
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error processing image from camera $currentCameraId: ${e.message}")
        } finally {
            image?.close()
        }
    }

    // Konvertiere YUV_420_888 zu NV21
    private fun YUV_420_888toNV21(image: Image): ByteArray {
        val width = image.width
        val height = image.height
        val ySize = width * height
        val uvSize = width * height / 4
        
        val nv21 = ByteArray(ySize + uvSize * 2)
        
        // Get the YUV planes
        val planes = image.planes
        val yBuffer = planes[0].buffer
        val uBuffer = planes[1].buffer
        val vBuffer = planes[2].buffer
        
        val yRowStride = planes[0].rowStride
        val yPixelStride = planes[0].pixelStride
        val uvRowStride = planes[1].rowStride
        val uvPixelStride = planes[1].pixelStride
        
        Log.d(TAG, "[Camera2Helper] Image format: YUV_420_888, size: ${width}x${height}")
        Log.d(TAG, "[Camera2Helper] Y plane: stride=${yRowStride}, pixelStride=${yPixelStride}")
        Log.d(TAG, "[Camera2Helper] UV planes: stride=${uvRowStride}, pixelStride=${uvPixelStride}")
        
        // Copy Y plane
        if (yPixelStride == 1) {
            // Fast path for contiguous data
            for (row in 0 until height) {
                yBuffer.position(row * yRowStride)
                yBuffer.get(nv21, row * width, width)
            }
        } else {
            // Slow path for non-contiguous data
            var yBufferPos = 0
            for (row in 0 until height) {
                for (col in 0 until width) {
                    nv21[row * width + col] = yBuffer.get(yBufferPos)
                    yBufferPos += yPixelStride
                }
                yBufferPos += yRowStride - width * yPixelStride
            }
        }
        
        // Copy UV data
        var pos = 0
        for (row in 0 until height / 2) {
            for (col in 0 until width / 2) {
                val uvBufferPos = row * uvRowStride + col * uvPixelStride
                nv21[ySize + pos] = vBuffer.get(uvBufferPos)      // V
                nv21[ySize + pos + 1] = uBuffer.get(uvBufferPos)  // U
                pos += 2
            }
        }
        
        return nv21
    }

    // Erweiterte Pixel-Analyse
    private fun analyzePixelData(data: ByteArray, width: Int, height: Int) {
        try {
            val ySize = width * height
            var yStats = PixelStats()
            var uStats = PixelStats()
            var vStats = PixelStats()
            
            // Y-Plane Analyse (Helligkeit)
            for (i in 0 until min(1000, ySize)) {  // Analysiere mehr Pixel
                val value = data[i].toInt() and 0xFF
                yStats.update(value)
            }
            
            // UV-Planes Analyse
            for (i in 0 until min(500, ySize / 4)) {
                val vValue = data[ySize + i * 2].toInt() and 0xFF
                val uValue = data[ySize + i * 2 + 1].toInt() and 0xFF
                vStats.update(vValue)
                uStats.update(uValue)
            }
            
            Log.d(TAG, """[Camera2Helper] Detailed Pixel Analysis (NV21):
                |Y-Plane (Brightness): min=${yStats.min}, max=${yStats.max}, avg=${yStats.average}, nonZero=${yStats.nonZeroCount}
                |U-Plane (Blue-Yellow): min=${uStats.min}, max=${uStats.max}, avg=${uStats.average}, nonNeutral=${uStats.nonNeutralCount}
                |V-Plane (Red-Green): min=${vStats.min}, max=${vStats.max}, avg=${vStats.average}, nonNeutral=${vStats.nonNeutralCount}
                |Frame appears to be: ${determineFrameState(yStats, uStats, vStats)}
                """.trimMargin())
            
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error analyzing pixel data: ${e.message}")
        }
    }

    private class PixelStats {
        var min = 255
        var max = 0
        var sum = 0L
        var count = 0
        var nonZeroCount = 0
        var nonNeutralCount = 0  // Für UV-Werte, die nicht 128 sind
        
        val average: Int get() = if (count > 0) (sum / count).toInt() else 0
        
        fun update(value: Int) {
            min = min(min, value)
            max = max(max, value)
            sum += value
            count++
            if (value > 0) nonZeroCount++
            if (value != 128) nonNeutralCount++
        }
    }

    private fun determineFrameState(yStats: PixelStats, uStats: PixelStats, vStats: PixelStats): String {
        return when {
            yStats.max == 0 -> "Completely black (no signal?)"
            yStats.min == 255 -> "Completely white (overexposed?)"
            yStats.average < 5 -> "Very dark (underexposed?)"
            yStats.average > 250 -> "Very bright (overexposed?)"
            uStats.nonNeutralCount == 0 && vStats.nonNeutralCount == 0 -> "Grayscale only (color disabled?)"
            yStats.nonZeroCount == 0 -> "No luminance data (camera error?)"
            else -> "Normal frame"
        }
    }

    // Erstelle eine Capture-Session
    private fun createCaptureSession() {
        try {
            val surface = imageReader?.surface
            if (surface == null) {
                Log.e(TAG, "[Camera2Helper] Surface is null!")
                return
            }

            Log.d(TAG, "[Camera2Helper] Creating capture session for camera: ${cameraDevice?.id}")
            
            // Wichtig: Setze die FPS explizit
            val captureBuilder = cameraDevice?.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW)?.apply {
                addTarget(surface)
                
                // Auto-Exposure aktivieren
                set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
                set(CaptureRequest.CONTROL_AE_MODE, CameraMetadata.CONTROL_AE_MODE_ON)
                
                // Frame Rate explizit setzen
                set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(30, 30))
            }?.build()

            if (captureBuilder == null) {
                Log.e(TAG, "[Camera2Helper] Failed to create capture request!")
                return
            }

            // Erstelle die Capture Session
            cameraDevice?.createCaptureSession(
                listOf(surface),
                object : CameraCaptureSession.StateCallback() {
                    override fun onConfigured(session: CameraCaptureSession) {
                        Log.d(TAG, "[Camera2Helper] Capture session configured")
                        captureSession = session
                        try {
                            session.setRepeatingRequest(
                                captureBuilder,
                                object : CameraCaptureSession.CaptureCallback() {
                                    override fun onCaptureCompleted(
                                        session: CameraCaptureSession,
                                        request: CaptureRequest,
                                        result: TotalCaptureResult
                                    ) {
                                        // Log jedes 30. Frame
                                        val timestamp = result.get(CaptureResult.SENSOR_TIMESTAMP)
                                        if ((timestamp ?: 0) % 30 == 0L) {
                                            Log.d(TAG, "[Camera2Helper] Frame captured, timestamp: $timestamp")
                                        }
                                    }
                                },
                                backgroundHandler
                            )
                            Log.d(TAG, "[Camera2Helper] Started repeating capture request")
                        } catch (e: Exception) {
                            Log.e(TAG, "[Camera2Helper] Failed to start repeating request: ${e.message}")
                        }
                    }

                    override fun onConfigureFailed(session: CameraCaptureSession) {
                        Log.e(TAG, "[Camera2Helper] Failed to configure capture session!")
                    }
                },
                backgroundHandler
            )
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error in createCaptureSession: ${e.message}")
        }
    }

    // Erstelle einen Capture-Request
    private fun createCaptureRequest(surface: Surface): CaptureRequest {
        return cameraDevice!!.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW).apply {
            addTarget(surface)
            
            // Automatische Belichtung aktivieren
            set(CaptureRequest.CONTROL_MODE, CameraMetadata.CONTROL_MODE_AUTO)
            set(CaptureRequest.CONTROL_AE_MODE, CameraMetadata.CONTROL_AE_MODE_ON)
            
            // Setze einen initialen ISO-Wert
            set(CaptureRequest.SENSOR_SENSITIVITY, 800) // ISO 800
            
            // Setze eine minimale Belichtungszeit
            set(CaptureRequest.SENSOR_EXPOSURE_TIME, 1000000L) // 1ms
            
            // Aktiviere Auto-White-Balance
            set(CaptureRequest.CONTROL_AWB_MODE, CameraMetadata.CONTROL_AWB_MODE_AUTO)
            
            Log.d(TAG, "[Camera2Helper] Capture request configured with auto exposure and ISO 800")
        }.build()
    }

    // Richte den ImageReader ein
    private fun setupImageReader() {
        try {
            if (backgroundHandler == null) {
                Log.e(TAG, "[Camera2Helper] Background handler is null, starting background thread first")
                startBackgroundThread()
                if (backgroundHandler == null) {
                    throw IllegalStateException("Failed to create background handler")
                }
            }
            
            imageReader = ImageReader.newInstance(
                frameWidth, 
                frameHeight, 
                ImageFormat.YUV_420_888, 
                MAX_IMAGES
            ).apply {
                setOnImageAvailableListener(imageReaderListener, backgroundHandler)
            }
            Log.d(TAG, "[Camera2Helper] ImageReader created with size: ${frameWidth}x${frameHeight}")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to setup ImageReader: ${e.message}", e)
            throw e
        }
    }

    // Initialisiere die Kamera mit der angegebenen ID und Auflösung
    fun initialize(width: Int, height: Int, cameraId: String) {
        try {
            Log.d(TAG, "[Camera2Helper] Initializing camera with ID: $cameraId, resolution: ${width}x${height}")
            
            // Setze die Auflösung
            frameWidth = width
            frameHeight = height
            
            // Setze die Kamera-ID
            currentCameraId = cameraId
            
            // Starte den Hintergrund-Thread
            startBackgroundThread()
            
            // Richte den ImageReader ein
            setupImageReader()
            
            isInitialized = true
            Log.d(TAG, "[Camera2Helper] Initialization complete with camera ID: $cameraId")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to initialize: ${e.message}", e)
            stopBackgroundThread()
        }
    }

    // Setze die Auflösung
    fun setResolution(width: Int, height: Int) {
        if (width <= 0 || height <= 0) {
            Log.e(TAG, "[Camera2Helper] Invalid resolution: ${width}x${height}")
            return
        }
        
        frameWidth = width
        frameHeight = height
        Log.d(TAG, "[Camera2Helper] Resolution set to ${width}x${height}")
    }

    // Starte die Kamera
    fun startCamera(cameraId: String) {
        try {
            checkCameraCapabilities(cameraId)
            if (!cameraOpenCloseLock.tryAcquire(CAMERA_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                throw RuntimeException("[Camera2Helper] Time out waiting to lock camera opening.")
            }
            if (currentCameraId == null) {
                Log.e(TAG, "[Camera2Helper] No camera selected, cannot start.")
                return
            }
            
            if (!isInitialized) {
                Log.e(TAG, "[Camera2Helper] Cannot start camera - not initialized!")
                return
            }
            
            try {
                // Prüfe Berechtigungen
                if (ActivityCompat.checkSelfPermission(context, Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
                    Log.e(TAG, "[Camera2Helper] Camera permission not granted")
                    return
                }
                
                // Prüfe Meta-spezifische Berechtigung
                try {
                    if (ActivityCompat.checkSelfPermission(context, HEADSET_CAMERA_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
                        Log.w(TAG, "[Camera2Helper] Meta headset camera permission not granted")
                    }
                } catch (e: Exception) {
                    Log.w(TAG, "[Camera2Helper] Meta headset camera permission check failed: ${e.message}")
                }
                
                // Öffne die Kamera
                Log.d(TAG, "[Camera2Helper] Starting camera with ID: $currentCameraId")
                cameraManager.openCamera(currentCameraId!!, cameraStateCallback, backgroundHandler)
            } catch (e: Exception) {
                Log.e(TAG, "[Camera2Helper] Failed to start camera: ${e.message}", e)
                cameraOpenCloseLock.release()
            }
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to start camera: ${e.message}", e)
            cameraOpenCloseLock.release()
        }
    }

    // Stoppe die Kamera
    fun stopCamera() {
        try {
            // Warte auf Freigabe der Kamera
            cameraOpenCloseLock.acquire()
            
            // Stoppe die Capture-Session
            captureSession?.close()
            captureSession = null
            
            // Schließe die Kamera
            cameraDevice?.close()
            cameraDevice = null
            
            // Schließe den ImageReader
            imageReader?.close()
            imageReader = null
            
            // Stoppe den Hintergrund-Thread
            stopBackgroundThread()
            
            Log.d(TAG, "[Camera2Helper] Camera stopped")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error stopping camera: ${e.message}", e)
        } finally {
            cameraOpenCloseLock.release()
        }
    }

    // Gib alle Ressourcen frei
    fun release() {
        stopCamera()
        isInitialized = false
        Log.d(TAG, "[Camera2Helper] Resources released")
    }

    // Hole alle verfügbaren Kamera-Konfigurationen
    fun getCameraConfigurations(): String {
        val configs = JSONArray()
        
        try {
            val cameraIds = cameraManager.cameraIdList
            
            for (cameraId in cameraIds) {
                val characteristics = cameraManager.getCameraCharacteristics(cameraId)
                val streamConfigMap = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                val sizes = streamConfigMap?.getOutputSizes(ImageFormat.YUV_420_888)
                
                // Versuche, Meta-spezifische Metadaten zu bekommen
                var metaCameraPosition = -1
                var metaCameraSource = -1
                
                try {
                    // Meta-spezifische Vendor-Tags (wie im Meta-Beispiel)
                    val metaCameraPositionKey = CameraCharacteristics.Key("com.meta.extra_metadata.position", Int::class.javaObjectType)
                    val metaCameraSourceKey = CameraCharacteristics.Key("com.meta.extra_metadata.camera_source", Int::class.javaObjectType)
                    
                    metaCameraPosition = characteristics.get(metaCameraPositionKey) ?: -1
                    metaCameraSource = characteristics.get(metaCameraSourceKey) ?: -1
                } catch (e: Exception) {
                    Log.w(TAG, "Meta-specific camera metadata not available: ${e.message}")
                }
                
                val lensFacing = characteristics.get(CameraCharacteristics.LENS_FACING)
                
                JSONObject().apply {
                    put("id", cameraId)
                    val largestSize = sizes?.maxByOrNull { it.width * it.height }
                    put("width", largestSize?.width ?: frameWidth)
                    put("height", largestSize?.height ?: frameHeight)
                    put("isLeftCamera", metaCameraPosition == 0)
                    put("isPassthroughCamera", metaCameraSource == 0)
                    put("lensFacing", lensFacing ?: -1)
                    configs.put(this)
                }
            }
            Log.d(TAG, "Camera configs JSON: ${configs.toString()}")
        } catch (e: Exception) {
            Log.e(TAG, "Failed to get camera configurations", e)
        }
        return configs.toString()
    }

    fun setImageCallback(callback: ImageCallback) {
        Log.d(TAG, "[Camera2Helper] Setting image callback.")
        if (!isInitialized) {
            Log.e(TAG, "[Camera2Helper] Cannot set callback - not initialized!")
        }
        imageCallback = callback
    }

    fun selectCameraById(cameraId: String) {
        Log.d(TAG, "[Camera2Helper] Camera ID set via initialize: $cameraId")
        if (cameraDevice != null || captureSession != null) {
            Log.w(TAG, "[Camera2Helper] Camera already running. Stopping before selecting new ID.")
            stopCamera()
        }
        currentCameraId = cameraId
    }

    fun switchCamera() {
        Log.e(TAG, "[Camera2Helper] switchCamera() is not supported. Use Unity-side camera switching instead.")
    }

    fun startPeriodicCameraSwitch(intervalMs: Long = 5000) {
        Log.e(TAG, "[Camera2Helper] startPeriodicCameraSwitch is not supported. Use Unity-side camera switching instead.")
    }

    fun stopPeriodicCameraSwitch() {
        Log.e(TAG, "[Camera2Helper] stopPeriodicCameraSwitch is not supported. Use Unity-side camera switching instead.")
    }

    fun requestFrame() {
        Log.d(TAG, "[Camera2Helper] requestFrame() is deprecated, using continuous capture.")
    }

    fun getCurrentCameraId(): String? {
        return currentCameraId
    }

    // Neue Methode zum Auflisten aller Kameras im Log
    fun logAllCameras() {
        try {
            Log.d(TAG, "[Camera2Helper] ===== ALLE VERFÜGBAREN KAMERAS =====")
            val cameraIds = cameraManager.cameraIdList
            
            if (cameraIds.isEmpty()) {
                Log.d(TAG, "[Camera2Helper] Keine Kameras gefunden!")
                return
            }
            
            Log.d(TAG, "[Camera2Helper] Gefundene Kameras: ${cameraIds.size}")
            
            for (cameraId in cameraIds) {
                Log.d(TAG, "[Camera2Helper] ------------------------------")
                Log.d(TAG, "[Camera2Helper] Kamera ID: $cameraId")
                
                val characteristics = cameraManager.getCameraCharacteristics(cameraId)
                
                // Kamera-Ausrichtung
                val facing = characteristics.get(CameraCharacteristics.LENS_FACING)
                val facingStr = when (facing) {
                    CameraCharacteristics.LENS_FACING_FRONT -> "FRONT"
                    CameraCharacteristics.LENS_FACING_BACK -> "BACK"
                    CameraCharacteristics.LENS_FACING_EXTERNAL -> "EXTERNAL"
                    else -> "UNKNOWN"
                }
                Log.d(TAG, "[Camera2Helper] Ausrichtung: $facingStr")
                
                // Verfügbare Auflösungen
                val configMap = characteristics.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
                val sizes = configMap?.getOutputSizes(ImageFormat.YUV_420_888)
                Log.d(TAG, "[Camera2Helper] Verfügbare Auflösungen (YUV_420_888):")
                sizes?.forEach { size ->
                    Log.d(TAG, "[Camera2Helper]   ${size.width} x ${size.height}")
                }
                
                // Meta-spezifische Metadaten
                try {
                    val allTags = characteristics.keys
                    Log.d(TAG, "[Camera2Helper] Alle verfügbaren Tags:")
                    allTags.forEach { key ->
                        Log.d(TAG, "[Camera2Helper]   ${key.name} (${key.id})")
                    }
                    
                    // Versuche, Meta-spezifische Tags zu finden
                    try {
                        val metaCameraPositionKey = CameraCharacteristics.Key("com.meta.extra_metadata.position", Int::class.javaObjectType)
                        val metaCameraSourceKey = CameraCharacteristics.Key("com.meta.extra_metadata.camera_source", Int::class.javaObjectType)
                        
                        val position = characteristics.get(metaCameraPositionKey)
                        val source = characteristics.get(metaCameraSourceKey)
                        
                        Log.d(TAG, "[Camera2Helper] Meta Position: $position (0=Left, 1=Right)")
                        Log.d(TAG, "[Camera2Helper] Meta Source: $source (0=Passthrough)")
                    } catch (e: Exception) {
                        Log.d(TAG, "[Camera2Helper] Meta-spezifische Tags nicht gefunden: ${e.message}")
                    }
                    
                    // Versuche, alle Vendor-Tags zu finden
                    try {
                        val vendorTags = characteristics.keys.filter { it.name.startsWith("com.") }
                        Log.d(TAG, "[Camera2Helper] Vendor-Tags:")
                        vendorTags.forEach { key ->
                            val value = characteristics.get(key)
                            Log.d(TAG, "[Camera2Helper]   ${key.name}: $value")
                        }
                    } catch (e: Exception) {
                        Log.d(TAG, "[Camera2Helper] Fehler beim Lesen der Vendor-Tags: ${e.message}")
                    }
                } catch (e: Exception) {
                    Log.e(TAG, "[Camera2Helper] Fehler beim Lesen der Kamera-Metadaten: ${e.message}")
                }
            }
            Log.d(TAG, "[Camera2Helper] ===== ENDE DER KAMERALISTE =====")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Fehler beim Auflisten der Kameras: ${e.message}", e)
        }
    }

    private fun checkCameraCapabilities(cameraId: String) {
        try {
            val characteristics = cameraManager.getCameraCharacteristics(cameraId)
            
            // Prüfe verfügbare Belichtungsmodi
            val aeAvailableModes = characteristics.get(CameraCharacteristics.CONTROL_AE_AVAILABLE_MODES)
            Log.d(TAG, "[Camera2Helper] Available AE modes: ${aeAvailableModes?.joinToString()}")
            
            // Prüfe ISO-Bereich
            val isoRange = characteristics.get(CameraCharacteristics.SENSOR_INFO_SENSITIVITY_RANGE)
            Log.d(TAG, "[Camera2Helper] ISO range: $isoRange")
            
            // Prüfe Belichtungszeit-Bereich
            val exposureRange = characteristics.get(CameraCharacteristics.SENSOR_INFO_EXPOSURE_TIME_RANGE)
            Log.d(TAG, "[Camera2Helper] Exposure time range: $exposureRange")
            
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to check camera capabilities: ${e.message}")
        }
    }
}