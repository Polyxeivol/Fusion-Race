﻿// #define FUSION_NETWORK_SCENE_MANAGER_TRACE

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Fusion {
 

  public abstract class NetworkSceneManagerBase : MonoBehaviour, INetworkSceneObjectProvider {

    private static WeakReference<NetworkSceneManagerBase> s_currentlyLoading = new WeakReference<NetworkSceneManagerBase>(null);

    public bool ShowHierarchyWindowOverlay = true;

    private IEnumerator _runningCoroutine;
    private bool _currentSceneOutdated = false;
    private Dictionary<Guid, NetworkObject> _sceneObjects = new Dictionary<Guid, NetworkObject>();
    private SceneRef _currentScene;

    public NetworkRunner Runner { get; private set; }


    protected virtual void OnEnable() {
#if UNITY_EDITOR
      if (ShowHierarchyWindowOverlay) {
        UnityEditor.EditorApplication.hierarchyWindowItemOnGUI += HierarchyWindowOverlay;
      }
#endif
    }

    protected virtual void OnDisable() {
#if UNITY_EDITOR
      UnityEditor.EditorApplication.hierarchyWindowItemOnGUI -= HierarchyWindowOverlay;
#endif
    }

    protected virtual void LateUpdate() {
      if (!Runner) {
        return;
      }

      // store the flag in case scene changes during the load; this supports scene toggling as well
      if (Runner.CurrentScene != _currentScene) {
        _currentSceneOutdated = true;
      }

      if (!_currentSceneOutdated || _runningCoroutine != null) {
        // busy or up to date
        return;
      }

      if (s_currentlyLoading.TryGetTarget(out var target)) {
        Assert.Check(target != this);
        if (!target) {
          LogWarn("");
          s_currentlyLoading.SetTarget(null);
        } else {
          LogTrace($"Waiting for {target} to finish loading");
          return;
        }
      }

      var prevScene = _currentScene;
      _currentScene = Runner.CurrentScene;
      _currentSceneOutdated = false;

      LogTrace($"Scene transition {prevScene}->{_currentScene}");
      _runningCoroutine = SwitchSceneWrapper(prevScene, _currentScene);
      StartCoroutine(_runningCoroutine);
    }

    internal static bool TryGetSceneRefFromPathInBuildSettings(string scenePath, out SceneRef sceneRef) {
      var result = SceneUtility.GetBuildIndexByScenePath(scenePath);
      if (result >= 0) {
        sceneRef = result;
        return true;
      } else {
        sceneRef = default;
        return false;
      }
    }

    public static bool IsScenePathOrNameEqual(Scene scene, string nameOrPath) {
      return scene.path == nameOrPath || scene.name == nameOrPath;
    }

    public static bool TryGetScenePathFromBuildSettings(SceneRef sceneRef, out string path) {
      if (sceneRef.IsValid) {
        path = SceneUtility.GetScenePathByBuildIndex(sceneRef);
        if (!string.IsNullOrEmpty(path)) {
          return true;
        }
      }
      path = string.Empty;
      return false;
    }

    public bool IsScenePathOrNameEqual(Scene scene, SceneRef sceneRef) {
      if (TryGetScenePathFromBuildSettings(sceneRef, out var path)) {
        return IsScenePathOrNameEqual(scene, path);
      } else {
        return false;
      }
    }

    public static List<NetworkObject> FindNetworkObjects(Scene scene, bool disable = true) {

      var networkObjects = new List<NetworkObject>();
      var gameObjects = scene.GetRootGameObjects();
      var result = new List<NetworkObject>();

      // get all root gameobjects and move them to this runners scene
      foreach (var go in gameObjects) {
        networkObjects.Clear();
        go.GetComponentsInChildren(networkObjects);

        foreach (var sceneObject in networkObjects) {
          if (sceneObject.Flags.IsSceneObject() && sceneObject.gameObject.activeInHierarchy) {
            Assert.Check(sceneObject.NetworkGuid.IsValid);
            result.Add(sceneObject);
            if (disable) {
              sceneObject.gameObject.SetActive(false);
            }
          }
        }
      }

      return result;
    }


    #region INetworkSceneObjectProvider

    void INetworkSceneObjectProvider.Initialize(NetworkRunner runner) {
      Initialize(runner);
    }

    void INetworkSceneObjectProvider.Shutdown(NetworkRunner runner) {
      Shutdown(runner);
    }

    bool INetworkSceneObjectProvider.IsReady(NetworkRunner runner) {
      Assert.Check(Runner == runner);
      if (_runningCoroutine != null) {
        return false;
      }
      if (_currentSceneOutdated) {
        return false;
      }
      if (runner.CurrentScene != _currentScene) {
        return false;
      }
      return true;
    }

    bool INetworkSceneObjectProvider.TryResolveSceneObject(NetworkRunner runner, Guid sceneObjectGuid, out NetworkObject instance) {
      Assert.Check(Runner == runner);
      return _sceneObjects.TryGetValue(sceneObjectGuid, out instance);
    }

    #endregion

    protected virtual void Initialize(NetworkRunner runner) {
      Assert.Check(!Runner);
      Runner = runner;
    }

    protected virtual void Shutdown(NetworkRunner runner) {
      Assert.Check(Runner == runner);
      Runner = null;
    }

    protected delegate void FinishedLoadingDelegate(IEnumerable<NetworkObject> sceneObjects);

    protected abstract IEnumerator SwitchScene(SceneRef prevScene, SceneRef newScene, FinishedLoadingDelegate finished);

    [System.Diagnostics.Conditional("FUSION_NETWORK_SCENE_MANAGER_TRACE")]
    protected void LogTrace(string msg) {
      Log.Debug($"[NetworkSceneManager] {(this != null ? this.name : "<destroyed>")}: {msg}");
    }

    protected void LogError(string msg) {
      Log.Error($"[NetworkSceneManager] {(this != null ? this.name : "<destroyed>")}: {msg}");
    }

    protected void LogWarn(string msg) {
      Log.Warn($"[NetworkSceneManager] {(this != null ? this.name : "<destroyed>")}: {msg}");
    }


    private IEnumerator SwitchSceneWrapper(SceneRef prevScene, SceneRef newScene) {
      bool finishCalled = false;
      Dictionary<Guid, NetworkObject> sceneObjects = new Dictionary<Guid, NetworkObject>();
      Exception error = null;
      FinishedLoadingDelegate callback = (objects) => {
        finishCalled = true;
        foreach (var obj in objects) {
          sceneObjects.Add(obj.NetworkGuid, obj);
        }
      };

      try {
        Assert.Check(!s_currentlyLoading.TryGetTarget(out _));
        s_currentlyLoading.SetTarget(this);
        Runner.InvokeSceneLoadStart();
        var coro = SwitchScene(prevScene, newScene, callback);

        for (bool next = true; next;) {
          try {
            next = coro.MoveNext();
          } catch (Exception ex) {
            error = ex;
            break;
          }

          if (next) {
            yield return coro.Current;
          }
        }
      } finally {
        Assert.Check(s_currentlyLoading.TryGetTarget(out var target) && target == this);
        s_currentlyLoading.SetTarget(null);

        LogTrace($"Corutine finished for {newScene}");
        _runningCoroutine = null;
      }

      if (error != null) {
        LogError($"Failed to switch scenes: {error}");
      } else if (!finishCalled) {
        LogError($"Failed to switch scenes: SwitchScene implementation did not invoke finished delegate");
      } else {
        _sceneObjects = sceneObjects;
        Runner.RegisterUniqueObjects(_sceneObjects.Values);
        Runner.InvokeSceneLoadDone();
      }
    }

#if UNITY_EDITOR
    private static Lazy<GUIStyle> s_hierarchyOverlayLabelStyle = new Lazy<GUIStyle>(() => {
      var result = new GUIStyle(UnityEditor.EditorStyles.miniBoldLabel);
      result.alignment = TextAnchor.MiddleRight;
      result.padding.right += 20;
      result.padding.bottom += 2;
      return result;
    });

    private void HierarchyWindowOverlay(int instanceId, Rect position) {
      if (!Runner) {
        return;
      }

      if (!Runner.MultiplePeerUnityScene.IsValid()) {
        return;
      }

      if (Runner.MultiplePeerUnityScene.GetHashCode() != instanceId) {
        return;
      }

      UnityEditor.EditorGUI.LabelField(position, Runner.name, s_hierarchyOverlayLabelStyle.Value);
    }
#endif
  }
}
