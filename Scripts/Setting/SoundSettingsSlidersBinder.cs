using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Sync UI sliders with saved volumes and push changes to SoundMixerManager.
/// Attach this to the Sound Settings panel (or any object in the UI).
/// </summary>
[DisallowMultipleComponent]
public class SoundSettingsSlidersBinder : MonoBehaviour
{
    [Header("Sliders (0..1)")]
    [SerializeField] private Slider masterSlider;
    [SerializeField] private Slider sfxSlider;
    [SerializeField] private Slider bgmSlider;

    private void OnEnable()
    {
        RefreshFromSaved();
    }

    public void RefreshFromSaved()
    {
        // Prefer manager getters (handles defaults). Fallback to PlayerPrefs if manager missing.
        float master = (SoundMixerManager.Instance != null) ? SoundMixerManager.Instance.GetMasterVolume() : PlayerPrefs.GetFloat("audio.master", 1f);
        float sfx = (SoundMixerManager.Instance != null) ? SoundMixerManager.Instance.GetSFXVolume() : PlayerPrefs.GetFloat("audio.sfx", 1f);
        float bgm = (SoundMixerManager.Instance != null) ? SoundMixerManager.Instance.GetBGMVolume() : PlayerPrefs.GetFloat("audio.bgm", 1f);

        if (masterSlider != null) masterSlider.SetValueWithoutNotify(master);
        if (sfxSlider != null) sfxSlider.SetValueWithoutNotify(sfx);
        if (bgmSlider != null) bgmSlider.SetValueWithoutNotify(bgm);
    }

    // Optional: you can wire these from Slider OnValueChanged as well.
    public void OnMasterChanged(float v)
    {
        if (SoundMixerManager.Instance != null) SoundMixerManager.Instance.SetMasterVolume(v);
    }

    public void OnSfxChanged(float v)
    {
        if (SoundMixerManager.Instance != null) SoundMixerManager.Instance.SetSFXVolume(v);
    }

    public void OnBgmChanged(float v)
    {
        if (SoundMixerManager.Instance != null) SoundMixerManager.Instance.SetBGMVolume(v);
    }
}

