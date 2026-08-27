using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.Audio;

/// <summary>
/// Trigger Zone ที่เช็คเมื่อหิน (RollingBoulder) แช่อยู่ในโซนครบตามเวลา
/// Open → รอ → ปิด Collider หิน → รอ → เปิด Collider กลับ + Close → รอ → ทำลายหิน
/// </summary>
[RequireComponent(typeof(Collider))]
public class BoulderTrapZone : NetworkBehaviour
{
    [Header("Settings")]
    [Tooltip("เวลาที่หินต้องแช่ในโซนก่อนจะเริ่ม process (วินาที)")]
    [SerializeField, Min(0.1f)] private float requiredStayDuration = 5f;

    [Tooltip("เวลารอหลังเล่น Open ก่อนปิด Collider หิน (วินาที)")]
    [SerializeField, Min(0f)] private float disableColliderDelay = 1f;

    [Tooltip("ระยะเวลาที่ Collider หินถูกปิด ก่อนเปิดกลับ + เล่น Close (วินาที)")]
    [SerializeField, Min(0f)] private float colliderDisabledDuration = 1f;

    [Tooltip("เวลารอหลังเล่น Close ก่อนทำลายหิน (วินาที)")]
    [SerializeField, Min(0f)] private float destroyDelay = 1f;

    [Header("Animation")]
    [Tooltip("Animator ที่จะเล่น trigger 'Open' / 'Close' (เช่น ประตูพื้น)")]
    [SerializeField] private Animator animator;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงตอนเล่นอนิเมชัน Open")]
    [SerializeField] private AudioClip openSound;
    [Tooltip("เสียงตอนเล่นอนิเมชัน Close")]
    [SerializeField] private AudioClip closeSound;
    [Tooltip("ดีเลย์ก่อนเล่นเสียง Close (วินาที)")]
    [SerializeField, Min(0f)] private float closeSoundDelay = 0f;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    // --- Runtime ---
    private readonly Dictionary<RollingBoulder, float> _boulderEnterTimes = new();
    private readonly HashSet<RollingBoulder> _processing = new();
    private bool _isDoorOpen = false;

    private void Awake()
    {
        InitializeAudio();
    }

    private void InitializeAudio()
    {
        if (audioSource == null) return;
        audioSource.playOnAwake = false;
        if (outputMixerGroup != null)
        {
            audioSource.outputAudioMixerGroup = outputMixerGroup;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;

        var boulder = other.GetComponent<RollingBoulder>();
        if (boulder == null) return;

        // ถ้าประตูกำลังเปิดอยู่ → ปิด collider หินทันที แล้วกำจัด
        if (_isDoorOpen)
        {
            if (!_processing.Contains(boulder))
            {
                _processing.Add(boulder);
                DisableBoulderCollidersClientRpc(boulder.NetworkObject);
                StartCoroutine(DestroyBoulderAfterDelay(boulder));
                Debug.Log($"[BoulderTrapZone] ประตูเปิดอยู่ → หิน {boulder.name} ตกลงไปทันที");
            }
            return;
        }

        if (!_boulderEnterTimes.ContainsKey(boulder))
        {
            _boulderEnterTimes[boulder] = Time.time;
            Debug.Log($"[BoulderTrapZone] หิน {boulder.name} เข้าโซน");
        }
    }

    private void OnTriggerStay(Collider other)
    {
        if (!IsServer) return;

        var boulder = other.GetComponent<RollingBoulder>();
        if (boulder == null) return;
        if (_processing.Contains(boulder)) return;

        // ประตูเปิดอยู่ → หินที่อยู่ในโซนตกลงไปทันที
        if (_isDoorOpen)
        {
            _processing.Add(boulder);
            DisableBoulderCollidersClientRpc(boulder.NetworkObject);
            StartCoroutine(DestroyBoulderAfterDelay(boulder));
            Debug.Log($"[BoulderTrapZone] ประตูเปิดอยู่ → หิน {boulder.name} (อยู่ในโซนแล้ว) ตกลงไปทันที");
            return;
        }

        if (_boulderEnterTimes.TryGetValue(boulder, out float enterTime))
        {
            float elapsed = Time.time - enterTime;
            if (elapsed >= requiredStayDuration)
            {
                _processing.Add(boulder);
                StartCoroutine(ProcessBoulder(boulder));
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer) return;

        var boulder = other.GetComponent<RollingBoulder>();
        if (boulder == null) return;

        if (!_processing.Contains(boulder))
        {
            _boulderEnterTimes.Remove(boulder);
            Debug.Log($"[BoulderTrapZone] หิน {boulder.name} ออกจากโซนก่อนครบเวลา");
        }
    }

    private IEnumerator ProcessBoulder(RollingBoulder boulder)
    {
        Debug.Log($"[BoulderTrapZone] หิน {boulder.name} แช่ครบ {requiredStayDuration} วิ → เริ่ม process");

        // 1. เล่นอนิเมชัน "Open"
        if (animator != null)
            PlayOpenAnimationClientRpc();

        // 2. รอ → ปิด Collider หิน + เปิดสถานะประตู
        yield return new WaitForSeconds(disableColliderDelay);
        DisableBoulderCollidersClientRpc(boulder.NetworkObject);
        _isDoorOpen = true;

        // 3. รอ → ปิดสถานะประตู + เปิด Collider หินกลับ + เล่นอนิเมชัน "Close"
        yield return new WaitForSeconds(colliderDisabledDuration);
        _isDoorOpen = false;
        EnableBoulderCollidersClientRpc(boulder.NetworkObject);

        if (animator != null)
            PlayCloseAnimationClientRpc();

        // 4. รอ → ทำลายหิน
        yield return new WaitForSeconds(destroyDelay);
        boulder.KillImmediateServer(playFx: false);

        // เคลียร์ state
        _boulderEnterTimes.Remove(boulder);
        _processing.Remove(boulder);

        Debug.Log($"[BoulderTrapZone] หินถูกทำลายแล้ว");
    }

    /// <summary>
    /// กำจัดหินที่เข้ามาขณะประตูเปิด (ไม่ต้องรอ 5 วิ)
    /// </summary>
    private IEnumerator DestroyBoulderAfterDelay(RollingBoulder boulder)
    {
        yield return new WaitForSeconds(destroyDelay);
        boulder.KillImmediateServer(playFx: false);

        _boulderEnterTimes.Remove(boulder);
        _processing.Remove(boulder);

        Debug.Log($"[BoulderTrapZone] หิน {boulder.name} (ตกขณะประตูเปิด) ถูกทำลายแล้ว");
    }

    // ===== ClientRpc =====

    [ClientRpc]
    private void DisableBoulderCollidersClientRpc(NetworkObjectReference boulderRef)
    {
        if (boulderRef.TryGet(out NetworkObject netObj))
        {
            foreach (var col in netObj.GetComponentsInChildren<Collider>())
                col.enabled = false;
        }
    }

    [ClientRpc]
    private void EnableBoulderCollidersClientRpc(NetworkObjectReference boulderRef)
    {
        if (boulderRef.TryGet(out NetworkObject netObj))
        {
            foreach (var col in netObj.GetComponentsInChildren<Collider>())
                col.enabled = true;
        }
    }

    [ClientRpc]
    private void PlayOpenAnimationClientRpc()
    {
        if (animator != null)
            animator.SetTrigger("Open");

        PlaySound(openSound);
    }

    [ClientRpc]
    private void PlayCloseAnimationClientRpc()
    {
        if (animator != null)
            animator.SetTrigger("Close");

        PlaySound(closeSound, closeSoundDelay);
    }

    private void PlaySound(AudioClip clip, float delay = 0f)
    {
        if (clip == null) return;
        if (delay > 0f)
        {
            StartCoroutine(PlaySoundDelayedCo(clip, delay));
            return;
        }
        if (audioSource != null)
        {
            audioSource.PlayOneShot(clip);
            return;
        }
        AudioSource.PlayClipAtPoint(clip, transform.position);
    }

    private IEnumerator PlaySoundDelayedCo(AudioClip clip, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (clip == null) yield break;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(clip);
            yield break;
        }

        AudioSource.PlayClipAtPoint(clip, transform.position);
    }
}
