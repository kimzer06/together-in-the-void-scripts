using UnityEngine;
using DG.Tweening;
using Unity.Netcode;
using UnityEngine.Audio;

public class DissolvingController : NetworkBehaviour
{
    public SkinnedMeshRenderer skinnedMesh;
    public float dissolveDuration = 2f;
    public Ease easeType = Ease.InQuad;
    
    public AudioClip dissolveSound;
    [Range(0f, 1f)] public float dissolveSoundVolume = 0.8f;
    public AudioMixerGroup sfxMixerGroup;
    [Tooltip("AudioSource สำหรับเล่นเสียง dissolve (ตั้งในซีน/พรีแฟบเอง ไม่สร้างให้อัตโนมัติ)")]
    public AudioSource dissolveAudioSource;

    private Material[] skinnedMaterials;
    private Tween dissolveTween;
    private bool _dissolveStarted = false;

    void Start()
    {
        // รองรับโหมดที่ไม่มี netcode (หรือ object ไม่ได้ spawn ผ่าน NetworkObject)
        BeginDissolveOnce(networked: false);
    }
    
    public override void OnNetworkSpawn()
    {
        // เมื่อเป็น networked object: ให้เริ่ม dissolve จากทุกฝั่งเหมือนกัน
        BeginDissolveOnce(networked: true);
    }

    private void BeginDissolveOnce(bool networked)
    {
        if (_dissolveStarted) return;
        _dissolveStarted = true;
        
        if (skinnedMesh != null)
        {
            skinnedMaterials = skinnedMesh.materials;
            Dissolve();
        }

        // เล่นเสียง + broadcast ให้ทุกคนได้ยิน (ให้ server ยิงครั้งเดียว กันเสียงซ้ำ)
        if (networked && IsSpawned)
        {
            if (IsServer)
            {
                PlayDissolveSfxClientRpc(transform.position);
            }
        }
        else
        {
            // offline / non-networked
            PlayLocalDissolveSfx(transform.position);
        }
    }

    void Dissolve()
    {
        if (skinnedMaterials == null || skinnedMaterials.Length == 0) return;

        float startValue = skinnedMaterials[0].GetFloat("_DissolveAmount");

        dissolveTween = DOTween.To(
            () => startValue,
            value =>
            {
                for (int i = 0; i < skinnedMaterials.Length; i++)
                {
                    skinnedMaterials[i].SetFloat("_DissolveAmount", value);
                }
            },
            1f,
            dissolveDuration
        ).SetEase(easeType);
    }

    private void PlayLocalDissolveSfx(Vector3 position)
    {
        if (dissolveSound == null) return;
        if (dissolveSoundVolume <= 0f) return;
        if (dissolveAudioSource == null) return;
        
        if (sfxMixerGroup != null)
            dissolveAudioSource.outputAudioMixerGroup = sfxMixerGroup;
        
        dissolveAudioSource.transform.position = position;
        dissolveAudioSource.volume = Mathf.Clamp01(dissolveSoundVolume);
        dissolveAudioSource.pitch = 1f;
        dissolveAudioSource.PlayOneShot(dissolveSound);
    }
    
    [ClientRpc]
    private void PlayDissolveSfxClientRpc(Vector3 position, ClientRpcParams clientRpcParams = default)
    {
        PlayLocalDissolveSfx(position);
    }

    void OnDestroy()
    {
        dissolveTween?.Kill();
    }
}