using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;

[DisallowMultipleComponent]
[RequireComponent(typeof(AudioSource))]
public class PersistentBGM : MonoBehaviour
{
    [System.Serializable]
    private struct SceneBgm
    {
        public string sceneName;
        public AudioClip clip;
        [Tooltip("ถ้าเข้า scene นี้ซ้ำ และ clip เดิมอยู่แล้ว จะบังคับเริ่มใหม่ (Play จากต้น)")]
        public bool restartIfAlreadyPlaying;
    }

    [Header("Persistence")]
    [SerializeField] private bool dontDestroyOnLoad = true;

    [Header("Scene → BGM")]
    [Tooltip("กำหนดเพลงตามชื่อ Scene (ตรงกับ Scene.name). ถ้าเจอชื่อซีนในลิสต์ จะสลับคลิปให้ทันทีตอนโหลดซีน")]
    [SerializeField] private SceneBgm[] sceneBgms;

    [Tooltip("ถ้าเปิด: เมื่อซีนอยู่ใน disallowScenes แต่มีการกำหนด BGM ใน Scene → BGM ให้เล่นได้ตามปกติ")]
    [SerializeField] private bool allowMappedBgmInDisallowedScenes = true;

    [Header("Fade")]
    [Tooltip("เฟดอินตอนเริ่ม Play เพลง (วินาที). 0 = ปิด")]
    [Min(0f)]
    [SerializeField] private float fadeInSeconds = 1.0f;

    [Header("Disallowed Scenes Behavior")]
    [Tooltip("ถ้าเปิด: เมื่อเข้า scene ในรายการ จะ Pause เพลงไว้ (กันเพลงซ้อน) และจะ UnPause เมื่อออกจาก scene เหล่านั้น")]
    [SerializeField] private bool pauseInDisallowScenes = true;

    [SerializeField] private string[] disallowScenes = new[] { "MainMenu", "StartScene", "Loading", "CharacterSelect", "EndCredits" };

    private static PersistentBGM _instance;
    private AudioSource _audio;
    private bool _pausedByThis;
    private Coroutine _resumeCo;
    private Coroutine _fadeCo;
    private float _baseVolume = 1f;
    private readonly HashSet<int> _playedClipInstanceIds = new HashSet<int>();

    private void Awake()
    {
        if (!dontDestroyOnLoad) return;

        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }

        _instance = this;
        _audio = GetComponent<AudioSource>();
        if (_audio != null) _baseVolume = _audio.volume;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_audio == null) _audio = GetComponent<AudioSource>();
        if (_audio == null) return;

        bool hasMappedBgm = TryApplySceneBgm(scene.name);

        // กันเสียงหลุดเสี้ยววินาทีตอนเริ่มซีน (BGM เป็น DontDestroyOnLoad + PlayOnAwake)
        // Pause ไว้ก่อน แล้วค่อยตัดสินใจ resume ตามกฎของ scene
        if (_resumeCo != null)
        {
            StopCoroutine(_resumeCo);
            _resumeCo = null;
        }

        if (_audio.isPlaying)
        {
            _audio.Pause();
            _pausedByThis = true;
        }

        bool disallowed = IsDisallowedScene(scene.name);

        if (pauseInDisallowScenes && disallowed && !(allowMappedBgmInDisallowedScenes && hasMappedBgm))
        {
            return;
        }

        // Leaving disallowed scenes (or general scene load) → resume next frame only if we paused it.
        if (_pausedByThis)
            _resumeCo = StartCoroutine(ResumeNextFrameIfStillAllowed());
    }

    private IEnumerator ResumeNextFrameIfStillAllowed()
    {
        // รอ 1 เฟรมให้ระบบเสียง/มิกเซอร์/ตัวควบคุม Timeline ได้ Apply ค่าแล้ว
        yield return null;

        if (_audio == null) yield break;

        var active = SceneManager.GetActiveScene();
        bool disallowed = IsDisallowedScene(active.name);
        if (pauseInDisallowScenes && disallowed) yield break;

        _audio.UnPause();
        _pausedByThis = false;
        _resumeCo = null;
    }

    private bool IsDisallowedScene(string sceneName)
    {
        if (disallowScenes == null) return false;
        for (int i = 0; i < disallowScenes.Length; i++)
        {
            if (string.Equals(disallowScenes[i], sceneName, System.StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private bool TryApplySceneBgm(string sceneName)
    {
        if (sceneBgms == null || sceneBgms.Length == 0) return false;
        if (_audio == null) return false;

        for (int i = 0; i < sceneBgms.Length; i++)
        {
            if (!string.Equals(sceneBgms[i].sceneName, sceneName, System.StringComparison.Ordinal))
                continue;

            var target = sceneBgms[i].clip;
            if (target == null) return true; // mapping exists but clip not assigned

            bool sameClip = _audio.clip == target;
            _audio.clip = target;

            if (!sameClip)
            {
                PlayWithFadeIn();
            }
            else if (sceneBgms[i].restartIfAlreadyPlaying)
            {
                PlayWithFadeIn();
            }

            return true;
        }

        return false;
    }

    private void PlayWithFadeIn()
    {
        if (_audio == null) return;

        if (_fadeCo != null)
        {
            StopCoroutine(_fadeCo);
            _fadeCo = null;
        }

        if (fadeInSeconds <= 0f)
        {
            _audio.volume = _baseVolume;
            _audio.Play();
            return;
        }

        var clip = _audio.clip;
        if (clip == null)
        {
            _audio.volume = _baseVolume;
            _audio.Play();
            return;
        }

        int clipId = clip.GetInstanceID();
        bool firstTimeForThisClip = !_playedClipInstanceIds.Contains(clipId);
        if (!firstTimeForThisClip)
        {
            _audio.volume = _baseVolume;
            _audio.Play();
            return;
        }

        _playedClipInstanceIds.Add(clipId);
        _fadeCo = StartCoroutine(FadeInRoutine());
    }

    private IEnumerator FadeInRoutine()
    {
        if (_audio == null) yield break;

        float targetVol = _baseVolume;
        _audio.volume = 0f;
        _audio.Play();

        float t = 0f;
        while (t < fadeInSeconds)
        {
            if (_audio == null) yield break;
            t += Time.unscaledDeltaTime;
            float a = Mathf.Clamp01(t / fadeInSeconds);
            _audio.volume = Mathf.Lerp(0f, targetVol, a);
            yield return null;
        }

        if (_audio != null) _audio.volume = targetVol;
        _fadeCo = null;
    }
}

