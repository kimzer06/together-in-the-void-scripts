using UnityEngine;
using Unity.Netcode;
using DG.Tweening;
using UnityEngine.Audio;

[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Rigidbody))] // เพิ่ม
public class MovingPlatform_Triggered : NetworkBehaviour
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    [Header("Debug")]
    [SerializeField] private bool showGizmos = true;

    [Header("Platform Movement Settings")]
    [SerializeField] private Axis moveAxis = Axis.X;
    [SerializeField, Min(0f)] private float moveAmount = 3f;
    [SerializeField] private Direction direction = Direction.Positive;
    [SerializeField] private float moveDuration = 1.2f;
    [SerializeField] private Ease moveEase = Ease.InOutSine;
    [SerializeField] private bool useLocalSpace = true;

    [Header("Emission Settings")]
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private int materialIndex = 0;
    [SerializeField, ColorUsage(false, true)] private Color emissionColor = Color.white;
    [SerializeField] private float emissionTransitionDuration = 0.3f;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงสั้น ๆ ตอนเริ่มขยับ (เล่นครั้งเดียว)")]
    [SerializeField] private AudioClip moveStartSound;
    [Tooltip("เสียง loop ระหว่างกำลังขยับ (ถ้าใส่จะ loop จนจบการเคลื่อนที่)")]
    [SerializeField] private AudioClip moveLoopSound;
    [Tooltip("เสียงสั้น ๆ ตอนหยุด/ถึงปลายทาง (เล่นครั้งเดียว)")]
    [SerializeField] private AudioClip moveEndSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    // === Exposure สำหรับผู้โดยสาร (player) ===
    public Vector3 Delta { get; private set; }     // การเปลี่ยนตำแหน่งของแพลตฟอร์มในเฟรมฟิสิกส์ล่าสุด
    public Vector3 Velocity { get; private set; }  // ความเร็วประมาณ (m/s)

    Vector3 startPosLocal, startPosWorld, endPos;
    Tween tween;
    Tween emissionTween;
    Rigidbody rb;
    Vector3 lastPos;  // ตำแหน่งเฟรมก่อนหน้า (world)
    Material cachedMaterial;
    static readonly int EmissionColorProperty = Shader.PropertyToID("_EmissionColor");
    private bool _isMovingAudio;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.isKinematic = true;              // สำคัญสำหรับแพลตฟอร์มที่ถูกขยับด้วยสคริปต์
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        startPosLocal = transform.localPosition;
        startPosWorld = transform.position;
        RecalculateEnd();

        lastPos = transform.position;

        // Cache material สำหรับ emission
        if (targetRenderer != null && targetRenderer.materials.Length > materialIndex)
        {
            cachedMaterial = targetRenderer.materials[materialIndex];
            cachedMaterial.EnableKeyword("_EMISSION");
        }

        InitializeAudio();
    }

    void FixedUpdate()
    {
        // คำนวณ delta/velocity ให้ผู้โดยสารนำไปใช้
        Vector3 now = transform.position;
        Delta = now - lastPos;
        Velocity = Delta / Time.fixedDeltaTime;
        lastPos = now;
    }

    void RecalculateEnd()
    {
        Vector3 axis = moveAxis == Axis.X ? Vector3.right :
                       moveAxis == Axis.Y ? Vector3.up : Vector3.forward;

        float dir = (direction == Direction.Positive) ? 1f : -1f;

        endPos = useLocalSpace
            ? startPosLocal + axis * (moveAmount * dir)
            : startPosWorld + axis * (moveAmount * dir);
    }

    void SetEmission(bool enabled)
    {
        if (cachedMaterial == null) return;

        emissionTween?.Kill();
        Color targetColor = enabled ? emissionColor : Color.black;

        emissionTween = DOTween.To(
            () => cachedMaterial.GetColor(EmissionColorProperty),
            x => cachedMaterial.SetColor(EmissionColorProperty, x),
            targetColor,
            emissionTransitionDuration
        ).SetEase(Ease.OutQuad);
    }

    // ===== Commands (เรียกจาก Server เท่านั้น) =====
    public void Extend()
    {
        if (!IsServer) return;
        tween?.Kill();
        SetEmission(true);
        StartMoveAudio();
        tween = (useLocalSpace
            ? transform.DOLocalMove(endPos, moveDuration)
            : transform.DOMove(endPos, moveDuration))
            .SetEase(moveEase)
            .SetUpdate(UpdateType.Fixed, true) // วิ่งใน FixedUpdate
            .OnComplete(StopMoveAudio);

        ExtendClientRpc();
    }

    public void Retract()
    {
        if (!IsServer) return;
        tween?.Kill();
        SetEmission(false);
        StartMoveAudio();
        tween = (useLocalSpace
            ? transform.DOLocalMove(startPosLocal, moveDuration)
            : transform.DOMove(startPosWorld, moveDuration))
            .SetEase(moveEase)
            .SetUpdate(UpdateType.Fixed, true);
        tween.OnComplete(StopMoveAudio);

        RetractClientRpc();
    }

    [ClientRpc]
    void ExtendClientRpc()
    {
        if (IsServer) return;
        tween?.Kill();
        SetEmission(true);
        StartMoveAudio();
        tween = (useLocalSpace
            ? transform.DOLocalMove(endPos, moveDuration)
            : transform.DOMove(endPos, moveDuration))
            .SetEase(moveEase)
            .SetUpdate(UpdateType.Fixed, true)
            .OnComplete(StopMoveAudio);
    }

    [ClientRpc]
    void RetractClientRpc()
    {
        if (IsServer) return;
        tween?.Kill();
        SetEmission(false);
        StartMoveAudio();
        tween = (useLocalSpace
            ? transform.DOLocalMove(startPosLocal, moveDuration)
            : transform.DOMove(startPosWorld, moveDuration))
            .SetEase(moveEase)
            .SetUpdate(UpdateType.Fixed, true)
            .OnComplete(StopMoveAudio);
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

    private void StartMoveAudio()
    {
        if (audioSource == null) return;

        // กันกรณีถูกสั่งซ้ำ ๆ แล้ว kill tween: ให้ reset loop ก่อนเริ่มใหม่
        StopMoveLoopIfNeeded();

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
    }

    private void StopMoveAudio()
    {
        StopMoveLoopIfNeeded();

        if (audioSource != null && moveEndSound != null)
        {
            audioSource.PlayOneShot(moveEndSound);
        }
    }

    private void StopMoveLoopIfNeeded()
    {
        if (audioSource == null) return;
        if (!_isMovingAudio) return;

        _isMovingAudio = false;
        audioSource.loop = false;
        if (audioSource.clip == moveLoopSound)
        {
            audioSource.Stop();
            audioSource.clip = null;
        }
    }

    void OnDestroy()
    {
        tween?.Kill();
        emissionTween?.Kill();
        StopMoveLoopIfNeeded();
    }

    void OnValidate()
    {
        if (Application.isPlaying) return;
        startPosLocal = transform.localPosition;
        startPosWorld = transform.position;
        RecalculateEnd();
    }

    void OnDrawGizmos()
    {
        if (!showGizmos) return;
        Vector3 start = useLocalSpace
            ? (Application.isPlaying ? startPosLocal : transform.localPosition)
            : (Application.isPlaying ? startPosWorld : transform.position);

        Gizmos.color = Color.green; Gizmos.DrawLine(start, endPos);
        Gizmos.color = Color.cyan; Gizmos.DrawWireSphere(start, 0.15f);
        Gizmos.color = Color.red; Gizmos.DrawWireSphere(endPos, 0.15f);
    }
}
