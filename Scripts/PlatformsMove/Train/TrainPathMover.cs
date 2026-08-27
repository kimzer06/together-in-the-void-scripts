using Unity.Netcode;
using UnityEngine;
using UnityEngine.Audio;

/// <summary>
/// วิ่งจาก Start → End (เส้นตรง) แบบ Server-authoritative
/// ถึง End แล้ว Despawn คืนเข้าพูล
/// ใช้ร่วมกับ NetworkTransform บน Prefab
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TrainPathMover : NetworkBehaviour, IFreezeListener
{
    [Header("Path (set by Spawner)")]
    public Transform startPoint;
    public Transform endPoint;

    [Header("Movement")]
    [Tooltip("หน่วย/วินาที (ใส่ 6000 ได้)")]
    public float speed = 20f;

    [Tooltip("ถือว่า 'ถึง' ถ้าระยะเหลือ ≤ ค่านี้")]
    public float arriveTolerance = 0.05f;

    [Tooltip("หมุนตามทิศวิ่งระหว่างเคลื่อนที่")]
    public bool rotateToVelocity = true;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับเสียงรถไฟ (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงสั้น ๆ ตอนเริ่มขยับ")]
    [SerializeField] private AudioClip moveStartSound;
    [Tooltip("เสียง loop ระหว่างกำลังขยับ")]
    [SerializeField] private AudioClip moveLoopSound;
    [Tooltip("เสียงสั้น ๆ ตอนหยุด (ถึงปลายทาง/ถูก despawn)")]
    [SerializeField] private AudioClip moveEndSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [HideInInspector] public TrainPathSpawner Spawner;

    private bool _isFrozen;
    private bool _isMovingAudio;

    public void OnFreezeChanged(bool on)
    {
        _isFrozen = on;
        UpdateAudioForFreeze();
    }

    public override void OnNetworkSpawn()
    {
        InitializeAudio();

        // เปิด component ทั้ง server/client เพื่อให้รับ freeze + คุมเสียงได้
        enabled = true;

        if (IsServer)
        {
            if (startPoint != null) transform.position = startPoint.position;
            StartMoveAudioClientRpc();
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer || _isFrozen) return;
        if (endPoint == null) { DespawnToPool(); return; }

        Vector3 pos = transform.position;
        Vector3 toTarget = endPoint.position - pos;
        float dist = toTarget.magnitude;

        if (dist <= arriveTolerance)
        {
            DespawnToPool();
            return;
        }

        float step = speed * Time.fixedDeltaTime;
        if (step >= dist)
        {
            transform.position = endPoint.position;
            DespawnToPool();
            return;
        }

        Vector3 dir = toTarget / dist;
        transform.position = pos + dir * step;

        if (rotateToVelocity && dir.sqrMagnitude > 1e-8f)
            transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
    }

    private void DespawnToPool()
    {
        if (!IsServer) return;

        StopMoveAudioClientRpc();
        Spawner?.NotifyTrainArrived();
        var netObj = GetComponent<NetworkObject>();
        if (netObj.IsSpawned) netObj.Despawn(false);
        gameObject.SetActive(false);
        Spawner?.ReturnToPool(netObj);
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

    [ClientRpc]
    private void StartMoveAudioClientRpc()
    {
        StartMoveAudio();
    }

    [ClientRpc]
    private void StopMoveAudioClientRpc()
    {
        StopMoveAudio();
    }

    private void StartMoveAudio()
    {
        if (audioSource == null) return;

        StopMoveAudio();

        if (moveStartSound != null)
        {
            audioSource.PlayOneShot(moveStartSound);
        }

        if (moveLoopSound != null)
        {
            _isMovingAudio = true;
            audioSource.loop = true;
            audioSource.clip = moveLoopSound;
            audioSource.Play();
        }

        UpdateAudioForFreeze();
    }

    private void StopMoveAudio()
    {
        if (audioSource == null) return;

        if (_isMovingAudio)
        {
            _isMovingAudio = false;
            audioSource.loop = false;
            if (audioSource.clip == moveLoopSound)
            {
                audioSource.Stop();
                audioSource.clip = null;
            }
        }

        if (moveEndSound != null)
        {
            audioSource.PlayOneShot(moveEndSound);
        }
    }

    private void UpdateAudioForFreeze()
    {
        if (audioSource == null) return;
        if (!_isMovingAudio) return;

        if (_isFrozen)
        {
            if (audioSource.isPlaying) audioSource.Pause();
        }
        else
        {
            if (!audioSource.isPlaying && audioSource.clip == moveLoopSound) audioSource.UnPause();
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        if (startPoint) Gizmos.DrawSphere(startPoint.position, 0.18f);
        if (endPoint)   Gizmos.DrawSphere(endPoint.position, 0.18f);
        if (startPoint && endPoint) Gizmos.DrawLine(startPoint.position, endPoint.position);
    }
#endif
}
