using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Android;
using UnityEngine.UI;

public class TestCamera2Helper : MonoBehaviour
{
    void Start()
    {
        Debug.Log("TestCamera2Helper started");
        StartCoroutine(DebugClassLoader());
    }

    IEnumerator DebugClassLoader()
    {
        yield return new WaitForSeconds(2);

        if (Application.platform == RuntimePlatform.Android)
        {
            try 
            {
                using (AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    Debug.Log("Got UnityPlayer  Camera2Helper class");
                    using (AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                    {
                        Debug.Log("Got Activity  Camera2Helper class");
                        
                        // Direkt den ClassLoader vom Activity-Context holen
                        using (AndroidJavaObject classLoader = activity.Call<AndroidJavaObject>("getClassLoader"))
                        {
                            Debug.Log($"ClassLoader  Camera2Helper class: {classLoader}");
                            
                            // Liste alle verfügbaren Klassen
                            try {
                                using (AndroidJavaClass dexFile = new AndroidJavaClass("dalvik.system.DexFile"))
                                {
                                    string apkPath = activity.Call<AndroidJavaObject>("getApplicationInfo").Get<string>("sourceDir");
                                    AndroidJavaObject dex = dexFile.CallStatic<AndroidJavaObject>("loadDex", apkPath, null, 0);
                                    AndroidJavaObject entries = dex.Call<AndroidJavaObject>("entries");
                                    
                                    while (entries.Call<bool>("hasMoreElements"))
                                    {
                                        string className = entries.Call<string>("nextElement");
                                        if (className.Contains("camera2"))
                                        {
                                            Debug.Log($"Found Camera2Helper class: {className}");
                                        }
                                        else
                                        {
                                        }
                                    }
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"Failed  Camera2Helper to list classes: {e}");
                            }

                            // Versuche die Camera2Helper-Klasse zu laden
                            try {
                                using (AndroidJavaObject cls = classLoader.Call<AndroidJavaObject>("loadClass", "com.tryar.camera2.Camera2Helper"))
                                {
                                    Debug.Log("Camera2Helper class found!");
                                }
                            }
                            catch (Exception e)
                            {
                                Debug.LogError($"Failed to load Camera2Helper class: {e}");
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"Debug  Camera2Helper failed: {e}");
                        StartCoroutine(DebugClassLoader());

            }
        }
    }
}