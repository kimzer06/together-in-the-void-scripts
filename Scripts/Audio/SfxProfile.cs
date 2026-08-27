using UnityEngine;
using UnityEngine.Audio;

[CreateAssetMenu(menuName = "TogetherInTheVoid/Audio/SFX Profile", fileName = "SfxProfile_")]
public class SfxProfile : ScriptableObject
{
    [Header("Clips")]
    public AudioClip[] clips;

    [Header("Mixer")]
    public AudioMixerGroup mixerGroup;

    [Header("Volume / Pitch")]
    [Range(0f, 1f)] public float volume = 1f;
    [Range(0f, 0.2f)] public float pitchRandom = 0.03f;

    [Header("3D")]
    [Range(0f, 1f)] public float spatialBlend = 1f;
    [Range(0f, 2f)] public float dopplerLevel = 0f;
    public float minDistance = 1.5f;
    public float maxDistance = 20f;
    public AudioRolloffMode rolloff = AudioRolloffMode.Logarithmic;

    public bool IsValid => clips != null && clips.Length > 0;
}

