using UnityEngine;
using UnityEngine.Audio;

public class SoundMixerManager : MonoBehaviour
{
    [SerializeField] private AudioMixer audioMixer;

    private const string MasterKey = "audio.master";
    private const string SfxKey = "audio.sfx";
    private const string BgmKey = "audio.bgm";

    // Avoid Log10(0) -> -Infinity
    private const float MinLinear = 0.0001f;
    private const float MutedDb = -80f;

    public static SoundMixerManager Instance { get; private set; }
    private int _cutsceneMuteDepth;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        ApplySavedVolumes();
    }

    private void OnEnable()
    {
        // If object is re-enabled (or scene loads additively), ensure mixer params are applied.
        ApplySavedVolumes();
    }

    public void PushCutsceneMute()
    {
        _cutsceneMuteDepth++;
        if (_cutsceneMuteDepth != 1) return;
        ApplyMixerDb("sfxVolume", MutedDb);
        ApplyMixerDb("bgmVolume", MutedDb);
    }

    public void PopCutsceneMute()
    {
        if (_cutsceneMuteDepth <= 0) return;
        _cutsceneMuteDepth--;
        if (_cutsceneMuteDepth != 0) return;
        ApplySavedVolumes();
    }

    public void SetMasterVolume(float level)
    {
        float v = Mathf.Clamp(level, MinLinear, 1f);
        PlayerPrefs.SetFloat(MasterKey, v);
        PlayerPrefs.Save();
        ApplyMixerFloat("masterVolume", v);
    }

    public void SetSFXVolume(float level)
    {
        float v = Mathf.Clamp(level, MinLinear, 1f);
        PlayerPrefs.SetFloat(SfxKey, v);
        PlayerPrefs.Save();
        ApplyMixerFloat("sfxVolume", v);
    }

    public void SetBGMVolume(float level)
    {
        float v = Mathf.Clamp(level, MinLinear, 1f);
        PlayerPrefs.SetFloat(BgmKey, v);
        PlayerPrefs.Save();
        ApplyMixerFloat("bgmVolume", v);
    }

    public float GetMasterVolume() => PlayerPrefs.GetFloat(MasterKey, 1f);
    public float GetSFXVolume() => PlayerPrefs.GetFloat(SfxKey, 1f);
    public float GetBGMVolume() => PlayerPrefs.GetFloat(BgmKey, 1f);

    private void ApplySavedVolumes()
    {
        ApplyMixerFloat("masterVolume", GetMasterVolume());
        ApplyMixerFloat("sfxVolume", GetSFXVolume());
        ApplyMixerFloat("bgmVolume", GetBGMVolume());

        // กันกรณีมี cutscene mute ค้างอยู่ แล้ว OnEnable/Awake มา ApplySavedVolumes ทับ
        if (_cutsceneMuteDepth > 0)
        {
            ApplyMixerDb("sfxVolume", MutedDb);
            ApplyMixerDb("bgmVolume", MutedDb);
        }
    }

    private void ApplyMixerFloat(string exposedParam, float linear01)
    {
        if (audioMixer == null) return;
        float v = Mathf.Clamp(linear01, MinLinear, 1f);
        audioMixer.SetFloat(exposedParam, Mathf.Log10(v) * 20f);
    }

    private void ApplyMixerDb(string exposedParam, float db)
    {
        if (audioMixer == null) return;
        audioMixer.SetFloat(exposedParam, db);
    }
}