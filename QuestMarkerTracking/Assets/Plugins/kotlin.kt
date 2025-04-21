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

class Camera2Helper(private val context: Context) {
    companion object {
        private const val TAG = "Camera2Helper"
        private const val CAMERA_TIMEOUT_MS = 2500L
        // Meta Headset Camera Permission
        private const val HEADSET_CAMERA_PERMISSION = "horizonos.permission.HEADSET_CAMERA"
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

    interface ImageCallback {
        fun onImageAvailable(data: ByteArray, width: Int, height: Int)
    }

    private val imageReaderListener = ImageReader.OnImageAvailableListener { reader ->
        var image: Image? = null
        try {
            image = reader.acquireLatestImage()
            if (image != null) {
                val now = System.currentTimeMillis()
                if (lastFrameTime > 0) {
                    Log.d(TAG, "[Camera2Helper] Frame received! Time since last frame: ${now - lastFrameTime}ms")
                } else {
                    Log.d(TAG, "[Camera2Helper] First frame received!")
                }
                lastFrameTime = now

                val width = image.width
                val height = image.height
                val nv21Data = YUV_420_888toNV21(image)
                
                // Pixel-Analyse für Debugging
                analyzePixelData(nv21Data)

                imageCallback?.onImageAvailable(nv21Data, width, height)
            }
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error processing image", e)
        } finally {
            image?.close()
        }
    }

    // Neue Methode zur Analyse der Pixeldaten
    private fun analyzePixelData(data: ByteArray) {
        try {
            var nonZeroY = 0
            var nonZeroUV = 0
            
            // Stichprobenartige Prüfung (100 Pixel)
            val step = data.size / 200
            for (i in 0 until 100) {
                val index = i * step
                if (index < data.size) {
                    // Y-Werte prüfen (erste 2/3 des Arrays)
                    if (index < data.size * 2/3 && data[index].toInt() and 0xFF != 0) {
                        nonZeroY++
                    }
                    // UV-Werte prüfen (letztes 1/3 des Arrays)
                    else if (index >= data.size * 2/3 && data[index].toInt() and 0xFF != 128) {
                        nonZeroUV++
                    }
                }
            }
            
            Log.d(TAG, "[Camera2Helper] Pixel Analysis: nonZeroY=$nonZeroY/100, nonZeroUV=$nonZeroUV/100")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error analyzing pixel data", e)
        }
    }

    fun initialize(width: Int, height: Int, cameraId: String) {
        Log.d(TAG, "[Camera2Helper] Initializing with Width: $width, Height: $height, CameraID: $cameraId")
        frameWidth = width
        frameHeight = height
        currentCameraId = cameraId
        
        // Starte den Hintergrund-Thread
        startBackgroundThread()
        
        // Erstelle den ImageReader
        setupImageReader()
    }
    
    // Neue Methode zum Einrichten des ImageReaders
    private fun setupImageReader() {
        // Bestehenden ImageReader schließen, falls vorhanden
        imageReader?.close()
        
        // Neuen ImageReader erstellen
        imageReader = ImageReader.newInstance(
            frameWidth, 
            frameHeight, 
            ImageFormat.YUV_420_888, 
            2
        ).apply {
            setOnImageAvailableListener(imageReaderListener, backgroundHandler)
        }
        
        Log.d(TAG, "[Camera2Helper] ImageReader created with size: ${frameWidth}x${frameHeight}")
    }

    fun startCamera() {
        if (currentCameraId == null) {
            Log.e(TAG, "[Camera2Helper] No camera selected, cannot start.")
            return
        }
        if (backgroundHandler == null) {
            Log.e(TAG, "[Camera2Helper] Background handler not initialized.")
            startBackgroundThread()
            if (backgroundHandler == null) return
        }

        if (ActivityCompat.checkSelfPermission(context, Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            Log.e(TAG, "[Camera2Helper] Android Camera permission not granted.")
            return
        }
        
        try {
            // Prüfe Meta-spezifische Berechtigung
            if (context.packageManager.hasSystemFeature("com.meta.feature.PASSTHROUGH_CAMERA")) {
                if (context.checkSelfPermission(HEADSET_CAMERA_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
                    Log.e(TAG, "[Camera2Helper] Meta Headset Camera permission ($HEADSET_CAMERA_PERMISSION) not granted.")
                    return
                }
            }
        } catch (e: Exception) {
            Log.w(TAG, "[Camera2Helper] Error checking Meta camera permission: ${e.message}")
            // Fahre fort, da dies möglicherweise kein Meta-Gerät ist
        }

        try {
            if (!cameraOpenCloseLock.tryAcquire(CAMERA_TIMEOUT_MS, TimeUnit.MILLISECONDS)) {
                throw RuntimeException("Time out waiting to lock camera opening.")
            }
            
            Log.d(TAG, "[Camera2Helper] Starting camera with ID: $currentCameraId")
            cameraManager.openCamera(currentCameraId!!, cameraStateCallback, backgroundHandler)
        } catch (e: CameraAccessException) {
            Log.e(TAG, "[Camera2Helper] Failed to open camera", e)
            cameraOpenCloseLock.release()
        } catch (e: InterruptedException) {
            Log.e(TAG, "[Camera2Helper] Interrupted while opening camera", e)
            cameraOpenCloseLock.release()
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to start camera: ${e.message}", e)
            cameraOpenCloseLock.release()
        }
    }

    fun stopCamera() {
        try {
            cameraOpenCloseLock.acquire()
            try {
                captureSession?.close()
                captureSession = null
                
                cameraDevice?.close()
                cameraDevice = null
                
                imageReader?.close()
                imageReader = null
                
                Log.d(TAG, "[Camera2Helper] Camera stopped")
            } finally {
                cameraOpenCloseLock.release()
            }
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error stopping camera", e)
        }
    }

    fun release() {
        stopCamera()
        stopBackgroundThread()
        Log.d(TAG, "[Camera2Helper] Resources released")
    }

    private fun startBackgroundThread() {
        try {
            backgroundThread = HandlerThread("CameraBackground").apply {
                start()
                backgroundHandler = Handler(looper)
            }
            Log.d(TAG, "[Camera2Helper] Background thread started")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to start background thread", e)
        }
    }

    private fun stopBackgroundThread() {
        try {
            backgroundThread?.quitSafely()
            backgroundThread?.join()
            backgroundThread = null
            backgroundHandler = null
            Log.d(TAG, "[Camera2Helper] Background thread stopped")
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Failed to stop background thread", e)
        }
    }

    private val cameraStateCallback = object : CameraDevice.StateCallback() {
        override fun onOpened(camera: CameraDevice) {
            Log.d(TAG, "[Camera2Helper] Camera opened successfully: ${camera.id}")
            cameraDevice = camera
            cameraOpenCloseLock.release()
            createCaptureSession()
        }

        override fun onDisconnected(camera: CameraDevice) {
            Log.w(TAG, "[Camera2Helper] Camera disconnected: ${camera.id}")
            cameraOpenCloseLock.release()
            camera.close()
            cameraDevice = null
        }

        override fun onError(camera: CameraDevice, error: Int) {
            Log.e(TAG, "[Camera2Helper] Camera device error $error for camera: ${camera.id}")
            cameraOpenCloseLock.release()
            camera.close()
            cameraDevice = null
        }
    }

    private val captureSessionStateCallback = object : CameraCaptureSession.StateCallback() {
        override fun onConfigured(session: CameraCaptureSession) {
            Log.d(TAG, "[Camera2Helper] Session configured, starting repeating request")
            captureSession = session
            try {
                // Erstelle die Capture Request
                val device = cameraDevice ?: throw IllegalStateException("Camera device is null")
                val builder = device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
                
                // Füge die Surface hinzu
                val surface = imageReader?.surface ?: throw IllegalStateException("ImageReader surface is null")
                builder.addTarget(surface)
                
                // Setze wichtige Parameter
                builder.set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE)
                builder.set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                builder.set(CaptureRequest.CONTROL_AWB_MODE, CaptureRequest.CONTROL_AWB_MODE_AUTO)
                
                // Für Meta Quest: Setze Frame-Rate auf 24-30 FPS
                builder.set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, Range(24, 30))
                
                // Starte den kontinuierlichen Capture-Request
                session.setRepeatingRequest(builder.build(), captureCallback, backgroundHandler)
            } catch (e: Exception) {
                Log.e(TAG, "[Camera2Helper] Failed to start capture request", e)
            }
        }

        override fun onConfigureFailed(session: CameraCaptureSession) {
            Log.e(TAG, "[Camera2Helper] Failed to configure capture session")
        }
    }

    private val captureCallback = object : CameraCaptureSession.CaptureCallback() {
        override fun onCaptureCompleted(
            session: CameraCaptureSession,
            request: CaptureRequest,
            result: TotalCaptureResult
        ) {
            // Hier könnten wir Metadaten aus dem Capture-Ergebnis verarbeiten
            // Für jetzt lassen wir es leer, da wir die Bilder über den ImageReader bekommen
        }
    }

    private fun createCaptureSession() {
        val localCameraDevice = cameraDevice
        val localImageReader = imageReader
        
        if (localCameraDevice == null) {
            Log.e(TAG, "[Camera2Helper] Cannot create capture session, cameraDevice is null.")
            return
        }
        if (localImageReader == null || localImageReader.surface == null) {
            Log.e(TAG, "[Camera2Helper] Cannot create capture session, imageReader or surface is null.")
            return
        }

        try {
            Log.d(TAG, "[Camera2Helper] Creating capture session...")
            val surface = localImageReader.surface
            
            // Verwende die klassische createCaptureSession-Methode
            localCameraDevice.createCaptureSession(
                listOf(surface),
                captureSessionStateCallback,
                backgroundHandler
            )
        } catch (e: CameraAccessException) {
            Log.e(TAG, "[Camera2Helper] Failed to create capture session", e)
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error creating capture session: ${e.message}", e)
        }
    }

    private fun YUV_420_888toNV21(image: Image): ByteArray {
        try {
            val width = image.width
            val height = image.height
            val ySize = width * height
            val uvSize = width * height / 2
            
            val nv21 = ByteArray(ySize + uvSize)
            
            // Get the YUV planes
            val planes = image.planes
            val yBuffer = planes[0].buffer
            val uBuffer = planes[1].buffer
            val vBuffer = planes[2].buffer
            
            // Get plane strides
            val yPixelStride = planes[0].pixelStride
            val yRowStride = planes[0].rowStride
            val uPixelStride = planes[1].pixelStride
            val uRowStride = planes[1].rowStride
            val vPixelStride = planes[2].pixelStride
            val vRowStride = planes[2].rowStride
            
            Log.d(TAG, "[Camera2Helper] Y plane: pixelStride=$yPixelStride, rowStride=$yRowStride")
            Log.d(TAG, "[Camera2Helper] U plane: pixelStride=$uPixelStride, rowStride=$uRowStride")
            Log.d(TAG, "[Camera2Helper] V plane: pixelStride=$vPixelStride, rowStride=$vRowStride")
            
            // Buffer sizes
            val yBufferSize = yBuffer.remaining()
            val uBufferSize = uBuffer.remaining()
            val vBufferSize = vBuffer.remaining()
            
            Log.d(TAG, "[Camera2Helper] Buffer sizes - Y: $yBufferSize, U: $uBufferSize, V: $vBufferSize")
            Log.d(TAG, "[Camera2Helper] Strides - UV row: $uRowStride, UV pixel: $uPixelStride")
            
            // Copy Y plane
            if (yPixelStride == 1) {
                // Fast path for contiguous data
                yBuffer.get(nv21, 0, ySize)
            } else {
                // Slow path for non-contiguous data
                var position = 0
                for (row in 0 until height) {
                    var rowOffset = row * yRowStride
                    for (col in 0 until width) {
                        nv21[position++] = yBuffer.get(rowOffset)
                        rowOffset += yPixelStride
                    }
                }
            }
            
            // Copy UV data
            var position = ySize
            if (uPixelStride == 2 && vPixelStride == 2) {
                // Fast path for standard NV21 format
                var row = 0
                while (row < height / 2) {
                    var offset = row * uRowStride
                    for (col in 0 until width / 2) {
                        nv21[position++] = vBuffer.get(offset)  // V first for NV21
                        nv21[position++] = uBuffer.get(offset)  // then U
                        offset += uPixelStride
                    }
                    row++
                }
            } else {
                // Slow path for non-standard format
                var row = 0
                while (row < height / 2) {
                    var vOffset = row * vRowStride
                    var uOffset = row * uRowStride
                    for (col in 0 until width / 2) {
                        nv21[position++] = vBuffer.get(vOffset)  // V first for NV21
                        nv21[position++] = uBuffer.get(uOffset)  // then U
                        vOffset += vPixelStride
                        uOffset += uPixelStride
                    }
                    row++
                }
            }
            
            Log.d(TAG, "[Camera2Helper] YUV conversion complete, buffer size: ${nv21.size}, image dimensions: ${width}x${height}")
            return nv21
            
        } catch (e: Exception) {
            Log.e(TAG, "[Camera2Helper] Error converting YUV to NV21", e)
            return ByteArray(0)
        }
    }

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
                    // Meta-spezifische Vendor-Tags
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
        Log.w(TAG, "[Camera2Helper] switchCamera() called in Kotlin. This should be handled in Unity by stopping, re-initializing with new ID, and starting.")
    }

    fun startPeriodicCameraSwitch(intervalMs: Long = 5000) {
         Log.w(TAG, "[Camera2Helper] startPeriodicCameraSwitch called in Kotlin. This should be handled in Unity.")
    }

    fun stopPeriodicCameraSwitch() {
         Log.w(TAG, "[Camera2Helper] stopPeriodicCameraSwitch called in Kotlin. This should be handled in Unity.")
    }

    fun requestFrame() {
        Log.d(TAG, "[Camera2Helper] requestFrame() is deprecated, using continuous capture.")
    }

    fun getCurrentCameraId(): String? {
        return currentCameraId
    }
}