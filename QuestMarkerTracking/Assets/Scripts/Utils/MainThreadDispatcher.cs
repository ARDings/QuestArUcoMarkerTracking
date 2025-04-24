using UnityEngine;
using System;
using System.Collections.Generic;
using System.Threading;

public class MainThreadDispatcher : MonoBehaviour
{
    private static MainThreadDispatcher _instance;
    private static readonly Queue<Action> _executionQueue = new Queue<Action>();
    private static readonly object _lock = new object();

    public static MainThreadDispatcher Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    if (_instance == null)
                    {
                        var go = new GameObject("MainThreadDispatcher");
                        _instance = go.AddComponent<MainThreadDispatcher>();
                        DontDestroyOnLoad(go);
                    }
                }
            }
            return _instance;
        }
    }

    private void Awake()
    {
        if (_instance == null)
        {
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else if (_instance != this)
        {
            Destroy(gameObject);
        }
    }

    private void Update()
    {
        lock(_executionQueue)
        {
            while (_executionQueue.Count > 0)
            {
                try
                {
                    _executionQueue.Dequeue().Invoke();
                }
                catch (Exception e)
                {
                    Debug.LogError($"Error executing action on main thread: {e.Message}\n{e.StackTrace}");
                }
            }
        }
    }

    public static void RunOnMainThread(Action action)
    {
        if (action == null) return;

        lock(_executionQueue)
        {
            _executionQueue.Enqueue(action);
        }
    }

    /// <summary>
    /// Führt eine Aktion im Hauptthread aus und wartet auf deren Abschluss
    /// </summary>
    public static void RunOnMainThreadAndWait(Action action)
    {
        if (action == null) return;

        // Wenn wir bereits im Hauptthread sind, führe die Aktion direkt aus
        if (Thread.CurrentThread.ManagedThreadId == 1)
        {
            action();
            return;
        }

        ManualResetEvent waitHandle = new ManualResetEvent(false);
        
        RunOnMainThread(() => {
            action();
            waitHandle.Set();
        });
        
        waitHandle.WaitOne();
    }
} 