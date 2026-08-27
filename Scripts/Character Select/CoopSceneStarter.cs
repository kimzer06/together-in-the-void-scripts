using UnityEngine;
using UnityEngine.Events;
using Unity.Netcode;
using System.Collections;
using UnityEngine.SceneManagement;

/// <summary>
/// Place this script on a NetworkObject in your gameplay scenes.
/// It ensures players wait on a black screen until EVERYONE has loaded the scene,
/// then fades in and triggers custom events (like playing a cutscene).
/// </summary>
public class CoopSceneStarter : NetworkBehaviour
{
    [Header("Scene Start Settings")]
    [SerializeField] private float fadeInDuration = 1.0f;
    [SerializeField] private float extraHoldTime = 0.5f; // Extra black screen time after everyone is ready

    [Header("Events (Triggered after Fade In starts)")]
    public UnityEvent OnSceneStartSynced;

    private void Awake()
    {
        // Instantly turn the screen black when the scene loads
        if (ScreenFader.I != null)
        {
            ScreenFader.I.InstantBlack();
        }
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Subscribe to scene events to know when everyone has finished loading
            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnSceneEvent += OnSceneEvent;
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.OnSceneEvent -= OnSceneEvent;
        }
    }

    private void OnSceneEvent(SceneEvent sceneEvent)
    {
        // LoadEventCompleted means ALL clients have successfully loaded the scene
        // We only care about this if it matches our current active scene
        if (sceneEvent.SceneEventType == SceneEventType.LoadEventCompleted && 
            sceneEvent.SceneName == SceneManager.GetActiveScene().name)
        {
            StartCoroutine(ServerStartSceneSequence());
        }
    }

    private IEnumerator ServerStartSceneSequence()
    {
        // Wait an extra brief moment to ensure clients are fully initialized locally
        yield return new WaitForSecondsRealtime(extraHoldTime);

        // Tell all clients to start the scene (Fade in + Trigger Events)
        StartSceneClientRpc();
    }

    [ClientRpc]
    private void StartSceneClientRpc()
    {
        if (ScreenFader.I != null)
        {
            ScreenFader.I.FadeIn(fadeInDuration);
        }

        // Trigger any custom cutscenes or playable directors here
        OnSceneStartSynced?.Invoke();
    }
}
