using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using System.Collections;
using TMPro;

public class LoadingScreenManager : NetworkBehaviour
{
    private static string s_targetScene;

    [Header("UI Elements")]
    [SerializeField] private Slider progressBar;
    [SerializeField] private TMP_Text progressText;
    
    [Header("Settings")]
    [SerializeField] private float fakeMinDuration = 2f; // Minimum time to show loading screen
    [SerializeField] private float fadeDuration = 0.5f;

    /// <summary>
    /// Call this from ANY scene to transition to the Loading scene,
    /// which will eventually load the targetScene.
    /// ONLY THE SERVER SHOULD CALL THIS.
    /// </summary>
    public static void LoadSceneNetworked(string targetScene)
    {
        if (!NetworkManager.Singleton.IsServer)
        {
            Debug.LogError("Only the server can call LoadingScreenManager.LoadSceneNetworked!");
            return;
        }

        s_targetScene = targetScene;
        
        // Start fading out on all clients, then have the server load the "Loading" scene
        var fader = ScreenFader.I;
        if (fader != null)
        {
            fader.FadeOut();
            // We use standard coroutine on whatever is available or delay it
            // Assuming the caller handles the delay if needed, 
            // but for safety, we just load it immediately. NGO handles the sync.
        }

        NetworkManager.Singleton.SceneManager.LoadScene("Loading", LoadSceneMode.Single);
    }

    private void Start()
    {
        // When we enter the Loading scene, make sure the screen is visible
        if (ScreenFader.I != null)
            ScreenFader.I.InstantClear();

        if (IsServer)
        {
            if (string.IsNullOrEmpty(s_targetScene))
            {
                Debug.LogWarning("No target scene set for LoadingScreenManager. Falling back to Gameplay.");
                s_targetScene = "Gameplay";
            }
            StartCoroutine(ServerLoadNextSceneRoutine());
        }
        else
        {
            // Client: Fake the progress while waiting for the server to load the next scene
            StartCoroutine(ClientFakeProgressRoutine());
        }
    }

    private IEnumerator ServerLoadNextSceneRoutine()
    {
        float elapsed = 0f;
        float progress = 0f;

        // Phase 1: Fake progress for the minimum duration (0% -> 90%)
        while (elapsed < fakeMinDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            progress = Mathf.Lerp(0f, 0.9f, elapsed / fakeMinDuration);
            UpdateUI(progress);
            yield return null;
        }

        UpdateUI(1f);
        yield return new WaitForSecondsRealtime(0.2f);

        // FadeOut before the actual scene load
        if (ScreenFader.I != null)
            ScreenFader.I.FadeOut(fadeDuration);
            
        yield return new WaitForSecondsRealtime(fadeDuration + 0.1f);

        // Tell NGO to load the target scene (this will automatically sync clients)
        NetworkManager.Singleton.SceneManager.LoadScene(s_targetScene, LoadSceneMode.Single);
    }

    private IEnumerator ClientFakeProgressRoutine()
    {
        float t = 0f;
        while (t < fakeMinDuration)
        {
            t += Time.unscaledDeltaTime;
            float p = Mathf.Lerp(0f, 0.95f, t / fakeMinDuration);
            UpdateUI(p);
            yield return null;
        }
        // The client will stay at 95% until the server forces the scene change.
    }

    private void UpdateUI(float value)
    {
        if (progressBar) progressBar.value = value;
        if (progressText) progressText.text = $"{Mathf.RoundToInt(value * 100)}%";
    }
}
