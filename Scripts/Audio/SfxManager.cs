using UnityEngine;

[DisallowMultipleComponent]
public class SfxManager : MonoBehaviour
{
    public static SfxManager Instance { get; private set; }

    [Header("Pool (shared)")]
    [Range(4, 64)] public int poolSize = 16;

    [Header("Profiles")]
    [Tooltip("โปรไฟล์เสียงตอน FreezeOn()")]
    public SfxProfile freezeProfile;

    private AudioSource[] _pool;
    private int _cursor;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        EnsurePool();
    }

    private void OnValidate()
    {
        poolSize = Mathf.Clamp(poolSize, 1, 128);
    }

    private void EnsurePool()
    {
        int size = Mathf.Clamp(poolSize, 1, 128);
        if (_pool != null && _pool.Length == size) return;

        // rebuild
        if (_pool != null)
        {
            for (int i = 0; i < _pool.Length; i++)
                if (_pool[i] != null) Destroy(_pool[i].gameObject);
        }

        _pool = new AudioSource[size];
        for (int i = 0; i < size; i++)
        {
            var go = new GameObject($"Sfx_{i:D2}");
            go.transform.SetParent(transform, false);
            var src = go.AddComponent<AudioSource>();
            src.playOnAwake = false;
            src.loop = false;
            _pool[i] = src;
        }
        _cursor = 0;
    }

    private AudioSource Next()
    {
        EnsurePool();
        if (_pool == null || _pool.Length == 0) return null;
        var src = _pool[_cursor];
        _cursor = (_cursor + 1) % _pool.Length;
        return src;
    }

    public void Play(SfxProfile profile, Vector3 position)
    {
        if (profile == null || !profile.IsValid) return;
        if (profile.volume <= 0f) return;

        var src = Next();
        if (src == null) return;

        var clips = profile.clips;
        var clip = clips[Random.Range(0, clips.Length)];
        if (clip == null) return;

        src.transform.position = position;
        src.outputAudioMixerGroup = profile.mixerGroup != null ? profile.mixerGroup : src.outputAudioMixerGroup;
        src.spatialBlend = Mathf.Clamp01(profile.spatialBlend);
        src.dopplerLevel = Mathf.Max(0f, profile.dopplerLevel);
        src.rolloffMode = profile.rolloff;
        src.minDistance = Mathf.Max(0.01f, profile.minDistance);
        src.maxDistance = Mathf.Max(src.minDistance + 0.01f, profile.maxDistance);
        src.volume = Mathf.Clamp01(profile.volume);
        src.pitch = 1f + (profile.pitchRandom > 0f ? Random.Range(-profile.pitchRandom, profile.pitchRandom) : 0f);

        // ไม่ใช้ Stop()+clip+Play เพื่อไม่ไปตัดเสียงอื่นที่อาจยังเล่นอยู่บน source เดียวกันในบางเคส
        // PlayOneShot จะซ้อนบน source เดิมได้ (แต่ถ้าอยากคุมเข้มเรื่อง polyphony ให้เพิ่ม limiter ภายหลัง)
        src.PlayOneShot(clip);
    }

    public static void PlayFreeze(Vector3 position)
    {
        if (Instance == null) return;
        Instance.Play(Instance.freezeProfile, position);
    }
}

