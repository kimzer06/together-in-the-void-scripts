using UnityEngine;
using Unity.Netcode;
using System.Collections;
using UnityEngine.Audio;

/// <summary>
/// เสา Puzzle ที่ตรวจจับการชนจาก RollingBoulder และแสดงผล emission
/// ประตูเริ่มเปิด → เมื่อหินชน → ปิดประตู + เรืองขาว
///   - Type ผิด: เรืองแดง + ทำลายหิน + เปิดประตูกลับ
///   - Type ถูก: เรืองขาวค้าง รอเสาอื่น
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(Collider))]
[DisallowMultipleComponent]
public class PuzzlePillar : NetworkBehaviour
{
    [Header("Pillar Settings")]
    [Tooltip("ลำดับที่ถูกต้องของเสานี้ (1, 2, 3, ...)")]
    [SerializeField] private int pillarOrder = 1;

    [Tooltip("Manager ที่จัดการ puzzle นี้")]
    [SerializeField] private PillarPuzzleManager puzzleManager;

    [Header("Boulder Type Filter")]
    [Tooltip("ประเภทหินที่ยอมรับ (ถ้าว่าง = ยอมรับทุกประเภท)")]
    [SerializeField] private string requiredBoulderType = "";

    [Tooltip("ถ้าเปิด จะเช็คว่าหินต้องตรงประเภทที่กำหนดเท่านั้น")]
    [SerializeField] private bool filterByBoulderType = false;

    [Header("Door Settings")]
    [Tooltip("GameObject ประตูที่จะขยับเมื่อเสาถูกชน")]
    [SerializeField] private Transform door;

    [Tooltip("Offset ที่ใช้ปิดประตู (Local Offset จากตำแหน่งเปิด ไปยังตำแหน่งปิด)")]
    [SerializeField] private Vector3 doorCloseOffset = new Vector3(0f, -3f, 0f);

    [Tooltip("ความเร็วในการขยับประตู (วินาที)")]
    [SerializeField, Min(0.05f)] private float doorMoveDuration = 0.5f;

    [Tooltip("ระยะเวลาเรืองแดงเมื่อ Type ผิด (วินาที) ก่อนเปิดประตูกลับ")]
    [SerializeField, Min(0.1f)] private float wrongTypeFailDuration = 1f;

    [Tooltip("ดีเลย์หลังปิดประตูแล้ว ก่อนตรวจเช็คประเภทหิน (วินาที)")]
    [SerializeField, Min(0f)] private float delayBeforeCheck = 1f;

    [Header("Audio (Door Move)")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงตอนประตูเริ่มขยับ")]
    [SerializeField] private AudioClip doorMoveSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    [Header("Emission Settings")]
    [Tooltip("Renderer ที่จะเปลี่ยนสี emission")]
    [SerializeField] private Renderer targetRenderer;

    [Tooltip("ชื่อ Shader Property สำหรับ Emission Color")]
    [SerializeField] private string emissionColorProperty = "_EmissionColor";

    [Tooltip("ความเข้มของ Emission")]
    [SerializeField, Min(0f)] private float emissionIntensity = 2f;

    [Header("Colors")]
    [Tooltip("สีที่เรืองเมื่อถูกชน")]
    [SerializeField] private Color glowColorOnHit = Color.white;

    [Tooltip("สีที่เรืองเมื่อลำดับถูกต้อง (สำเร็จ)")]
    [SerializeField] private Color glowColorSuccess = Color.green;

    [Tooltip("สีที่เรืองเมื่อลำดับผิด (ล้มเหลว)")]
    [SerializeField] private Color glowColorFail = Color.red;

    public string RequiredBoulderType => requiredBoulderType;

    // --- Runtime State ---
    private MaterialPropertyBlock _propBlock;
    private bool _hasBeenHit = false;
    private int _emissionColorId;

    // Door state (ประตูเริ่ม "เปิด" = ตำแหน่งเดิม)
    private Vector3 _doorOpenLocalPos;   // ตำแหน่งเปิด (เดิม)
    private Vector3 _doorClosedLocalPos; // ตำแหน่งปิด
    private Coroutine _doorMoveCo;

    // เก็บอ้างอิงหินที่ชน เพื่อทำลายทีหลังถ้าลำดับผิด
    private RollingBoulder _storedBoulder;

    public int PillarOrder => pillarOrder;
    public bool HasBeenHit => _hasBeenHit;

    void Awake()
    {
        _propBlock = new MaterialPropertyBlock();
        _emissionColorId = Shader.PropertyToID(emissionColorProperty);

        if (targetRenderer == null)
            targetRenderer = GetComponent<Renderer>();

        // ตำแหน่งเริ่มต้น = ประตูเปิด
        if (door != null)
        {
            _doorOpenLocalPos = door.localPosition;
            _doorClosedLocalPos = _doorOpenLocalPos + doorCloseOffset;
        }

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

    public override void OnNetworkSpawn()
    {
        // เริ่มต้นด้วยการปิด emission, ประตูเปิด (ตำแหน่งเดิม)
        SetEmissionLocal(Color.black);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer) return;

        var boulder = collision.gameObject.GetComponentInParent<RollingBoulder>();
        if (boulder == null) return;

        if (_hasBeenHit) return;

        // ทุกกรณี: เรืองขาว + ปิดประตู แล้วรอดีเลย์ก่อนตรวจเช็ค
        _hasBeenHit = true;
        SetEmissionClientRpc(glowColorOnHit.r, glowColorOnHit.g, glowColorOnHit.b);
        CloseDoor();

        StartCoroutine(HandleHitAfterDelay(boulder));
    }

    private IEnumerator HandleHitAfterDelay(RollingBoulder boulder)
    {
        // รอดีเลย์หลังปิดประตู
        if (delayBeforeCheck > 0f)
            yield return new WaitForSeconds(delayBeforeCheck);

        // เช็คประเภทหิน
        if (filterByBoulderType && !string.IsNullOrEmpty(requiredBoulderType))
        {
            if (!string.Equals(boulder.BoulderType, requiredBoulderType, System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log($"[PuzzlePillar] {name} - หิน '{boulder.BoulderType}' ไม่ตรงกับที่ต้องการ '{requiredBoulderType}'");
                StartCoroutine(WrongTypeSequence(boulder));
                yield break;
            }
        }

        // Type ถูก → เก็บอ้างอิงหิน + แจ้ง Manager
        _storedBoulder = boulder;

        if (puzzleManager != null)
        {
            puzzleManager.Server_OnPillarHit(this);
        }
        else
        {
            Debug.LogWarning($"[PuzzlePillar] {name} - puzzleManager ไม่ได้ถูกกำหนด!");
        }
    }

    /// <summary>
    /// ทำลายหินที่เก็บไว้ (เรียกจาก Manager เมื่อลำดับผิด)
    /// </summary>
    public void DestroyStoredBoulder()
    {
        if (_storedBoulder != null)
        {
            _storedBoulder.KillImmediateServer();
            _storedBoulder = null;
        }
    }

    /// <summary>
    /// ลำดับเมื่อหิน Type ผิด: เรืองแดง → ทำลายหิน → รอ → ดับ + เปิดประตู
    /// </summary>
    private IEnumerator WrongTypeSequence(RollingBoulder boulder)
    {
        // เรืองแดง
        SetEmissionClientRpc(glowColorFail.r, glowColorFail.g, glowColorFail.b);

        // ทำลายหิน
        if (boulder != null)
            boulder.KillImmediateServer();

        // รอ
        yield return new WaitForSeconds(wrongTypeFailDuration);

        // ดับ emission + เปิดประตูกลับ + พร้อมรับหินใหม่
        SetEmissionClientRpc(0f, 0f, 0f);
        OpenDoor();
        _hasBeenHit = false;
    }

    // ====================== Door Movement ======================

    /// <summary>
    /// ปิดประตู (ย้ายไปตำแหน่ง closed)
    /// </summary>
    public void CloseDoor()
    {
        StartDoorMove(toClosed: true);
    }

    /// <summary>
    /// เปิดประตู (ย้ายกลับตำแหน่งเดิม)
    /// </summary>
    public void OpenDoor()
    {
        StartDoorMove(toClosed: false);
    }

    private void StartDoorMove(bool toClosed)
    {
        if (door == null) return;

        if (_doorMoveCo != null)
            StopCoroutine(_doorMoveCo);
        _doorMoveCo = StartCoroutine(MoveDoorCoroutine(toClosed));

        if (IsServer)
        {
            PlayDoorMoveSoundClientRpc();
        }
    }

    private IEnumerator MoveDoorCoroutine(bool toClosed)
    {
        if (door == null) yield break;

        Vector3 from = door.localPosition;
        Vector3 to = toClosed ? _doorClosedLocalPos : _doorOpenLocalPos;

        float elapsed = 0f;
        while (elapsed < doorMoveDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(elapsed / doorMoveDuration));
            door.localPosition = Vector3.Lerp(from, to, t);
            yield return null;
        }

        door.localPosition = to;
        _doorMoveCo = null;
    }

    [ClientRpc]
    private void PlayDoorMoveSoundClientRpc()
    {
        PlayDoorMoveSound();
    }

    private void PlayDoorMoveSound()
    {
        if (doorMoveSound == null) return;

        if (audioSource != null)
        {
            audioSource.PlayOneShot(doorMoveSound);
            return;
        }

        // fallback: เล่นที่ตำแหน่งเสา/ประตู
        Vector3 pos = door != null ? door.position : transform.position;
        AudioSource.PlayClipAtPoint(doorMoveSound, pos);
    }

    /// <summary>
    /// รีเซ็ตประตูทันทีไปตำแหน่งเปิด (ไม่มี animation)
    /// </summary>
    public void ResetDoorImmediate()
    {
        if (door == null) return;
        if (_doorMoveCo != null)
        {
            StopCoroutine(_doorMoveCo);
            _doorMoveCo = null;
        }
        door.localPosition = _doorOpenLocalPos;
    }

    // ====================== Emission ======================

    public void SetSuccessGlow()
    {
        if (IsServer)
            SetEmissionClientRpc(glowColorSuccess.r, glowColorSuccess.g, glowColorSuccess.b);
    }

    public void SetFailGlow()
    {
        if (IsServer)
            SetEmissionClientRpc(glowColorFail.r, glowColorFail.g, glowColorFail.b);
    }

    public void TurnOffGlow()
    {
        if (IsServer)
            SetEmissionClientRpc(0f, 0f, 0f);
    }

    /// <summary>
    /// รีเซ็ตสถานะเสาเพื่อเล่นใหม่ (เรืองดับ + เปิดประตู)
    /// </summary>
    public void ResetPillar()
    {
        _hasBeenHit = false;
        _storedBoulder = null;
        OpenDoor();

        if (IsServer)
            SetEmissionClientRpc(0f, 0f, 0f);
    }

    [ClientRpc]
    private void SetEmissionClientRpc(float r, float g, float b)
    {
        SetEmissionLocal(new Color(r, g, b));
    }

    private void SetEmissionLocal(Color color)
    {
        if (targetRenderer == null) return;

        Color emissionColor = color * emissionIntensity;

        targetRenderer.GetPropertyBlock(_propBlock);
        _propBlock.SetColor(_emissionColorId, emissionColor);
        targetRenderer.SetPropertyBlock(_propBlock);

        Material mat = targetRenderer.material;
        if (color == Color.black)
        {
            mat.DisableKeyword("_EMISSION");
        }
        else
        {
            mat.EnableKeyword("_EMISSION");
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (pillarOrder < 1) pillarOrder = 1;
    }

    private void OnDrawGizmosSelected()
    {
        if (door == null) return;

        Vector3 openWorld = door.parent != null
            ? door.parent.TransformPoint(door.localPosition)
            : door.localPosition;
        Vector3 closedWorld = door.parent != null
            ? door.parent.TransformPoint(door.localPosition + doorCloseOffset)
            : door.localPosition + doorCloseOffset;

        Gizmos.color = new Color(1f, 0.5f, 0f, 0.6f);
        Gizmos.DrawLine(openWorld, closedWorld);
        Gizmos.DrawWireSphere(closedWorld, 0.2f);

        UnityEditor.Handles.Label(openWorld + Vector3.up * 0.3f, "OPEN");
        UnityEditor.Handles.Label(closedWorld + Vector3.up * 0.3f, "CLOSED");
    }
#endif
}
