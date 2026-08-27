using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine.Audio;
using DG.Tweening;

[RequireComponent(typeof(NetworkObject))]
public class DoorAxisActivatableNet : NetworkBehaviour, IActivatable, IFreezeListener
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    [Header("Axis & Motion")]
    [Tooltip("Transform ที่จะขยับ (ถ้าไม่กำหนด = ขยับตัวเอง, ใช้กับ Group/Parent ได้)\n" +
             "⚠️ หมายเหตุ:\n" +
             "1. NetworkTransform ควรอยู่บน object นี้ (targetTransform) เพื่อ sync ตำแหน่งผ่าน network\n" +
             "2. อย่า mark object นี้หรือ child objects เป็น Static! (จะทำให้ไม่ขยับตาม parent และเกิด Combined Mesh)\n" +
             "3. ถ้า child objects มี Rigidbody ควรตั้งค่า isKinematic = true")]
    [SerializeField] private Transform targetTransform;
    [SerializeField] private Axis moveAxis = Axis.Y;
    [SerializeField] private Direction direction = Direction.Positive; // << เลือก +/− ที่นี่
    [SerializeField, Min(0f)] private float moveAmount = 3f;
    [SerializeField, Min(0f)] private float moveDuration = 1f;
    [SerializeField] private Ease moveEase = Ease.OutCubic;
    [SerializeField] private bool useLocalSpace = true;

    [Header("Start State")]
    [SerializeField] private bool startOpen = false;

    [Header("Auto Return Mode")]
    [Tooltip("เปิดใช้โหมดค้างแล้วกลับอัตโนมัติ (เมื่อเปิดแล้วจะค้างตามเวลาที่กำหนด แล้วค่อยๆ กลับมา)")]
    [SerializeField] private bool useAutoReturn = false;
    [Tooltip("ระยะเวลาที่จะค้างหลังจากเปิด (วินาที)")]
    [SerializeField, Min(0f)] private float holdDuration = 2f;
    [Tooltip("ระยะเวลาที่จะใช้ในการกลับมาที่จุดเริ่มต้น (วินาที)")]
    [SerializeField, Min(0f)] private float returnDuration = 1f;
    [Tooltip("Ease curve สำหรับการกลับมา")]
    [SerializeField] private Ease returnEase = Ease.InCubic;

    [Header("Auto Loop Mode")]
    [Tooltip("เปิดใช้โหมดลูปอัตโนมัติ (ขยับไป-กลับแบบลูปตั้งแต่เริ่มเกม)")]
    [SerializeField] private bool useAutoLoop = false;
    [Tooltip("ระยะเวลาที่จะหยุดขยับเมื่อถูก activate (วินาที) - ใช้กับสวิตช์")]
    [SerializeField, Min(0f)] private float pauseDurationOnActivate = 5f;

    [Header("Linked Doors (Co-op Freeze)")]
    [Tooltip("ลิงค์ DoorAxisActivatableNet ตัวอื่น — เมื่อโดน Time Freeze จะหยุดพร้อมกันทั้งหมด\n" +
             "ใส่ได้หลายตัว และไม่ต้องลิงค์กลับ (สคริปต์จัดการสองทางให้อัตโนมัติ)")]
    [SerializeField] private List<DoorAxisActivatableNet> linkedDoors = new();

    [Header("Debug")]
    [SerializeField] private bool showGizmos = true;

    [Header("Audio")]
    [Tooltip("Audio Source สำหรับตำแหน่งเกิดเสียง (กำหนดใน Inspector)")]
    [SerializeField] private AudioSource audioSource;
    [Tooltip("เสียงสั้น ๆ ตอนเริ่มขยับ (เล่นครั้งเดียว)")]
    [SerializeField] private AudioClip moveStartSound;
    [Tooltip("เสียง loop ระหว่างกำลังขยับ (ถ้าใส่จะ loop จนจบการเคลื่อนที่)")]
    [SerializeField] private AudioClip moveLoopSound;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อ Platform ถึงปลายทาง")]
    [SerializeField] private AudioClip endedSound;
    [Tooltip("Audio Clip ที่จะเล่นเมื่อ Platform กลับมาถึงจุดเริ่มต้น")]
    [SerializeField] private AudioClip returnSound;
    [Tooltip("Audio Mixer Group สำหรับส่ง Output (ถ้าต้องการ)")]
    [SerializeField] private AudioMixerGroup outputMixerGroup;

    Vector3 _startLocal, _startWorld;
    Vector3 _endLocal, _endWorld;
    bool _isOpen;
    Tween _tween;
    Coroutine _autoReturnCo;
    Coroutine _autoLoopCo;
    Coroutine _pauseResumeCo;
    bool _isPaused = false;
    bool _shouldPauseAtStart = false; // flag สำหรับบอกว่าต้องการ pause เมื่อกลับมาที่จุดเริ่มต้น
    bool _isMovingAudio;

    /// <summary>หยุดเวลา (Time Freeze) — ควรวาง FreezableNet บน GameObject เดียวกันเพื่อให้ถูกเรียกจาก FreezableNet</summary>
    bool _timeFrozen;
    bool _propagatingFreeze; // กัน infinite loop เมื่อ linked doors ส่งต่อกัน

    void Awake()
    {
        // ถ้าไม่กำหนด targetTransform ให้ใช้ตัวเอง
        if (targetTransform == null)
            targetTransform = transform;
        
        // ตรวจสอบว่า targetTransform มี NetworkTransform หรือไม่ (แนะนำให้มี)
        if (targetTransform != transform && !targetTransform.TryGetComponent<NetworkTransform>(out _))
        {
            Debug.LogWarning($"[{name}] ⚠️ targetTransform '{targetTransform.name}' ไม่มี NetworkTransform component! " +
                           "ควรเพิ่ม NetworkTransform บน object นี้เพื่อ sync ตำแหน่งผ่าน network อย่างถูกต้อง", targetTransform);
        }
        
        // ตรวจสอบ Colliders และ Rigidbodies ใน hierarchy
        ValidateHierarchy();
        
        CacheStarts();
        RecalculateEnd();
        InitializeAudio();
    }

    private void InitializeAudio()
    {
        if (audioSource != null)
        {
            audioSource.playOnAwake = false;
            audioSource.loop = false;
            if (outputMixerGroup != null)
            {
                audioSource.outputAudioMixerGroup = outputMixerGroup;
            }
        }
    }

    private void StartMoveAudio()
    {
        if (audioSource == null) return;

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

    private void ValidateHierarchy()
    {
        // ตรวจสอบ Static flag - สาเหตุหลักที่ทำให้ object ไม่ขยับตาม parent!
        CheckStaticFlags();
        
        // ตรวจสอบ Colliders และ Rigidbodies ใน hierarchy ของ targetTransform
        Collider[] allColliders = targetTransform.GetComponentsInChildren<Collider>(true);
        foreach (var col in allColliders)
        {
            if (col == null) continue;
            
            // ตรวจสอบ Rigidbody บน object ที่มี collider
            // ถ้ามี Rigidbody ที่ไม่ใช่ kinematic → อาจทำให้ไม่ขยับตาม parent
            Rigidbody rb = col.GetComponent<Rigidbody>();
            if (rb != null && !rb.isKinematic)
            {
                Debug.LogWarning($"[{name}] ⚠️ Object '{col.name}' มี Rigidbody ที่ไม่ใช่ kinematic! " +
                               "ควรตั้งค่า isKinematic = true เพื่อให้ขยับตาม parent ได้ถูกต้อง\n" +
                               "หรือถ้าไม่ต้องการใช้ Rigidbody ให้ลบออก", col);
            }
        }
        
        // ตรวจสอบว่า targetTransform มี Collider หรือไม่ (ถ้าไม่มี อาจจะต้องมี Collider บน child)
        if (targetTransform.GetComponent<Collider>() == null)
        {
            Collider[] childColliders = targetTransform.GetComponentsInChildren<Collider>(true);
            if (childColliders.Length == 0)
            {
                Debug.LogWarning($"[{name}] ⚠️ targetTransform '{targetTransform.name}' และ child objects ไม่มี Collider! " +
                               "ควรเพิ่ม Collider บน targetTransform หรือ child object เพื่อให้สามารถตรวจจับการชนได้", targetTransform);
            }
        }
    }

    private void CheckStaticFlags()
    {
        // ตรวจสอบ targetTransform เอง
        if (targetTransform.gameObject.isStatic)
        {
            Debug.LogError($"[{name}] ❌ targetTransform '{targetTransform.name}' ถูก mark เป็น Static! " +
                          "Static objects จะไม่ขยับตาม parent!\n" +
                          "วิธีแก้: Uncheck Static flag ใน Inspector (ด้านบนขวา)", targetTransform);
        }
        
        // ตรวจสอบ child objects ทั้งหมด
        Transform[] allChildren = targetTransform.GetComponentsInChildren<Transform>(true);
        foreach (var child in allChildren)
        {
            if (child == null || child == targetTransform) continue;
            
            if (child.gameObject.isStatic)
            {
                Debug.LogError($"[{name}] ❌ Child object '{child.name}' ถูก mark เป็น Static! " +
                              "Static objects จะไม่ขยับตาม parent และอาจถูกรวมเป็น Combined Mesh (root:scene)!\n" +
                              "วิธีแก้: Uncheck Static flag ใน Inspector (ด้านบนขวา) ของ object นี้", child);
            }
        }
        
        // ตรวจสอบ Combined Mesh objects ที่อาจถูกสร้างขึ้น (ถ้าพบใน scene)
        // Combined Mesh มักมีชื่อว่า "Combined Mesh (root:scene)" หรือคล้ายๆ กัน
        GameObject[] rootObjects = targetTransform.root.gameObject.scene.GetRootGameObjects();
        foreach (var rootObj in rootObjects)
        {
            if (rootObj == null) continue;
            string objName = rootObj.name.ToLower();
            if (objName.Contains("combined mesh") && objName.Contains("root"))
            {
                // ตรวจสอบว่า Combined Mesh นี้เกี่ยวข้องกับ targetTransform หรือไม่
                // โดยตรวจสอบว่ามี mesh renderer ที่ใช้ mesh เดียวกันหรือไม่
                MeshRenderer combinedRenderer = rootObj.GetComponent<MeshRenderer>();
                if (combinedRenderer != null)
                {
                    MeshRenderer[] targetRenderers = targetTransform.GetComponentsInChildren<MeshRenderer>(true);
                    foreach (var targetRenderer in targetRenderers)
                    {
                        if (targetRenderer != null && targetRenderer.sharedMaterial == combinedRenderer.sharedMaterial)
                        {
                            Debug.LogError($"[{name}] ❌ พบ Combined Mesh '{rootObj.name}' ที่อาจเกิดจาก Static Batching! " +
                                          "Combined Mesh จะไม่ขยับตาม parent!\n" +
                                          "วิธีแก้: Uncheck Static flag (โดยเฉพาะ Batching Static) บน child objects ทั้งหมด", rootObj);
                            break;
                        }
                    }
                }
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        
        if (useAutoLoop)
        {
            // โหมดลูป: เริ่มจากตำแหน่งเริ่มต้น
            SetInstant(false);
            _isOpen = false;
            // เริ่มลูปอัตโนมัติ
            _autoLoopCo = StartCoroutine(AutoLoopCo());
        }
        else
        {
            // โหมดปกติ: ตั้งสถานะเริ่ม "แบบทันที" ไม่เล่นแอนิเมชัน
            SetInstant(startOpen);
            _isOpen = startOpen;
        }
    }

    void CacheStarts()
    {
        _startLocal = targetTransform.localPosition;
        _startWorld = targetTransform.position;
    }

    void RecalculateEnd()
    {
        Vector3 axis =
            moveAxis == Axis.X ? Vector3.right :
            moveAxis == Axis.Y ? Vector3.up : Vector3.forward;

        float dir = (direction == Direction.Positive) ? 1f : -1f;

        _endLocal = _startLocal + axis * (moveAmount * dir);
        _endWorld = _startWorld + axis * (moveAmount * dir);
    }

    // ========== IActivatable ==========
    public void Activate(bool on)
    {
        if (!IsServer) return;

        // ถ้าเป็นโหมดลูป: on = true หมายถึงหยุดลูปชั่วคราว
        if (useAutoLoop)
        {
            if (on)
            {
                // ตรวจสอบว่า Platform อยู่ที่จุดเริ่มต้นหรือไม่
                bool isAtStart = useLocalSpace 
                    ? Vector3.Distance(targetTransform.localPosition, _startLocal) < 0.01f
                    : Vector3.Distance(targetTransform.position, _startWorld) < 0.01f;
                
                if (isAtStart)
                {
                    // อยู่ที่จุดเริ่มต้นแล้ว → pause ทันที
                    _isPaused = true;
                    _tween?.Kill();
                    
                    // หยุด coroutine pause/resume เก่า (ถ้ามี)
                    if (_pauseResumeCo != null)
                    {
                        StopCoroutine(_pauseResumeCo);
                    }
                    
                    // เริ่ม coroutine เพื่อกลับมาลูปต่อหลังจาก pauseDurationOnActivate
                    _pauseResumeCo = StartCoroutine(PauseAndResumeLoopCo());
                }
                else
                {
                    // ยังไม่ถึงจุดเริ่มต้น → ตั้ง flag ให้ pause เมื่อกลับมาที่จุดเริ่มต้น
                    _shouldPauseAtStart = true;
                    
                    // หยุด coroutine pause/resume เก่า (ถ้ามี)
                    if (_pauseResumeCo != null)
                    {
                        StopCoroutine(_pauseResumeCo);
                    }
                }
            }
            // on = false ไม่ต้องทำอะไร (รอให้หมดเวลา pause เอง)
            return;
        }

        // โหมดปกติ (เดิม)
        if (_isOpen == on) return;

        // หยุด auto-return coroutine ที่กำลังทำงานอยู่
        if (_autoReturnCo != null)
        {
            StopCoroutine(_autoReturnCo);
            _autoReturnCo = null;
        }
        _tween?.Kill();

        if (on)
        {
            // เปิด → ขยับไปตำแหน่งปลายทาง
            StartMoveAudioClientRpc();
            if (useLocalSpace)
            {
                _tween = targetTransform.DOLocalMove(_endLocal, moveDuration).SetEase(moveEase);
            }
            else
            {
                _tween = targetTransform.DOMove(_endWorld, moveDuration).SetEase(moveEase);
            }

            _tween.OnComplete(() => PlayReturnSoundClientRpc());

            _isOpen = true;

            // ถ้าเปิดใช้ auto-return → เริ่ม coroutine
            if (useAutoReturn)
            {
                _autoReturnCo = StartCoroutine(AutoReturnCo());
            }
        }
        else
        {
            // ปิด → ขยับกลับตำแหน่งเริ่มต้น
            StartMoveAudioClientRpc();
            if (useLocalSpace)
            {
                _tween = targetTransform.DOLocalMove(_startLocal, moveDuration).SetEase(moveEase);
            }
            else
            {
                _tween = targetTransform.DOMove(_startWorld, moveDuration).SetEase(moveEase);
            }

            _tween.OnComplete(() => PlayEndedSoundClientRpc());

            _isOpen = false;
        }
    }

    public void OnFreezeChanged(bool on)
    {
        if (on)
        {
            if (_timeFrozen) return;
            _timeFrozen = true;
            if (_tween != null && _tween.IsActive() && _tween.IsPlaying())
                _tween.Pause();
        }
        else
        {
            if (!_timeFrozen) return;
            _timeFrozen = false;
            if (_tween != null && _tween.IsActive() && !_tween.IsPlaying() && !_tween.IsComplete())
                _tween.Play();
        }

        // ── ส่งต่อ freeze/unfreeze ไปยัง linked doors ──
        if (!_propagatingFreeze)
        {
            _propagatingFreeze = true;
            foreach (var door in linkedDoors)
            {
                if (door != null && door != this)
                    door.OnFreezeChanged(on);
            }
            _propagatingFreeze = false;
        }
    }

    /// <summary>รอจน tween จบ — รองรับ Time Freeze (Pause/Play) และโหมดลูปที่ฆ่า tween เมื่อสวิตช์ pause</summary>
    private IEnumerator YieldWhileTweenIncomplete(bool killTweenIfSwitchPaused)
    {
        while (_tween != null && _tween.IsActive() && !_tween.IsComplete())
        {
            if (killTweenIfSwitchPaused && _isPaused)
            {
                _tween.Kill();
                yield break;
            }

            if (_timeFrozen && _tween.IsActive() && _tween.IsPlaying())
                _tween.Pause();
            else if (!_timeFrozen && _tween.IsActive() && !_tween.IsPlaying() && !_tween.IsComplete())
                _tween.Play();

            yield return null;
        }
    }

    private IEnumerator WaitSecondsRespectingFreeze(float seconds)
    {
        float t = 0f;
        while (t < seconds)
        {
            if (!_timeFrozen)
                t += Time.deltaTime;
            yield return null;
        }
    }

    private IEnumerator AutoReturnCo()
    {
        // รอให้การขยับไปตำแหน่งปลายทางเสร็จก่อน (ไม่ค้างถ้า tween ถูก pause จาก Time Freeze)
        yield return StartCoroutine(YieldWhileTweenIncomplete(false));

        // ค้างตามเวลาที่กำหนด (นับเวลาเฉพาะตอนไม่ถูก Time Freeze)
        yield return StartCoroutine(WaitSecondsRespectingFreeze(holdDuration));

        // กลับมาที่จุดเริ่มต้น
        StartMoveAudioClientRpc();
        if (useLocalSpace)
        {
            _tween = targetTransform.DOLocalMove(_startLocal, returnDuration).SetEase(returnEase);
        }
        else
        {
            _tween = targetTransform.DOMove(_startWorld, returnDuration).SetEase(returnEase);
        }

        _tween.OnComplete(() => PlayEndedSoundClientRpc());

        yield return StartCoroutine(YieldWhileTweenIncomplete(false));

        // อัปเดตสถานะ
        _isOpen = false;
        _autoReturnCo = null;
    }

    private IEnumerator AutoLoopCo()
    {
        while (true)
        {
            // รอจนกว่าจะไม่ถูก pause (สวิตช์) หรือ Time Freeze
            while (_isPaused || _timeFrozen)
            {
                yield return null;
            }

            // ขยับไปตำแหน่งปลายทาง
            StartMoveAudioClientRpc();
            if (useLocalSpace)
            {
                _tween = targetTransform.DOLocalMove(_endLocal, moveDuration).SetEase(moveEase);
            }
            else
            {
                _tween = targetTransform.DOMove(_endWorld, moveDuration).SetEase(moveEase);
            }

            // OnComplete fires ก่อน auto-kill จึงทำงานได้ถูกต้อง
            _tween?.OnComplete(() => { if (!_isPaused) PlayReturnSoundClientRpc(); });
            yield return StartCoroutine(YieldWhileTweenIncomplete(true));

            // รอจนกว่าจะไม่ถูก pause (สวิตช์) หรือ Time Freeze
            while (_isPaused || _timeFrozen)
            {
                yield return null;
            }

            // ขยับกลับตำแหน่งเริ่มต้น
            StartMoveAudioClientRpc();
            if (useLocalSpace)
            {
                _tween = targetTransform.DOLocalMove(_startLocal, moveDuration).SetEase(moveEase);
            }
            else
            {
                _tween = targetTransform.DOMove(_startWorld, moveDuration).SetEase(moveEase);
            }

            // รอให้ tween เสร็จ (สวิตช์ไม่ฆ่า tween ระหว่างขากลับ — แต่ Time Freeze ยังหยุดแอนิเมชันได้)
            _tween?.OnComplete(() => { if (!_shouldPauseAtStart) PlayEndedSoundClientRpc(); });
            yield return StartCoroutine(YieldWhileTweenIncomplete(false));
            
            // ตรวจสอบว่าต้องการ pause หรือไม่ (เมื่อกลับมาที่จุดเริ่มต้นแล้ว)
            if (_shouldPauseAtStart)
            {
                _shouldPauseAtStart = false;
                _isPaused = true;
                
                // เริ่ม coroutine เพื่อกลับมาลูปต่อหลังจาก pauseDurationOnActivate
                _pauseResumeCo = StartCoroutine(PauseAndResumeLoopCo());
            }
        }
    }

    private IEnumerator PauseAndResumeLoopCo()
    {
        yield return StartCoroutine(WaitSecondsRespectingFreeze(pauseDurationOnActivate));
        
        // กลับมาลูปต่อ
        _isPaused = false;
        _pauseResumeCo = null;
        
        // ตรวจสอบว่า AutoLoopCo ยังทำงานอยู่หรือไม่ ถ้าไม่ให้เริ่มใหม่
        if (_autoLoopCo == null)
        {
            _autoLoopCo = StartCoroutine(AutoLoopCo());
        }
    }

    [ClientRpc]
    private void StartMoveAudioClientRpc()
    {
        StartMoveAudio();
    }

    [ClientRpc]
    private void PlayEndedSoundClientRpc()
    {
        StopMoveLoopIfNeeded();
        if (audioSource != null && endedSound != null)
        {
            audioSource.PlayOneShot(endedSound);
        }
    }

    [ClientRpc]
    private void PlayReturnSoundClientRpc()
    {
        StopMoveLoopIfNeeded();
        if (audioSource != null && returnSound != null)
        {
            audioSource.PlayOneShot(returnSound);
        }
    }

    // ใช้ตอนเริ่มเกมเพื่อเซ็ตตำแหน่งทันที (ไม่ tween)
    void SetInstant(bool open)
    {
        _tween?.Kill();
        if (useLocalSpace)
            targetTransform.localPosition = open ? _endLocal : _startLocal;
        else
            targetTransform.position = open ? _endWorld : _startWorld;
    }

    public override void OnNetworkDespawn()
    {
        _tween?.Kill();
        StopMoveLoopIfNeeded();
        if (_autoReturnCo != null)
        {
            StopCoroutine(_autoReturnCo);
            _autoReturnCo = null;
        }
        if (_autoLoopCo != null)
        {
            StopCoroutine(_autoLoopCo);
            _autoLoopCo = null;
        }
        if (_pauseResumeCo != null)
        {
            StopCoroutine(_pauseResumeCo);
            _pauseResumeCo = null;
        }
    }

    private void OnDisable()
    {
        _tween?.Kill();
        StopMoveLoopIfNeeded();
        if (_autoReturnCo != null)
        {
            StopCoroutine(_autoReturnCo);
            _autoReturnCo = null;
        }
        if (_autoLoopCo != null)
        {
            StopCoroutine(_autoLoopCo);
            _autoLoopCo = null;
        }
        if (_pauseResumeCo != null)
        {
            StopCoroutine(_pauseResumeCo);
            _pauseResumeCo = null;
        }
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (!Application.isPlaying)
        {
            // ถ้าไม่กำหนด targetTransform ให้ใช้ตัวเอง
            if (targetTransform == null)
                targetTransform = transform;
            
            CacheStarts();
            RecalculateEnd();
        }
    }

    void OnDrawGizmos()
    {
        if (!showGizmos) return;

        // ใช้ targetTransform หรือ transform ตัวเอง
        Transform gizmoTarget = targetTransform != null ? targetTransform : transform;

        // ใช้ค่าปัจจุบันในโหมดแก้ไข เพื่อให้ gizmo อัปเดตสด
        Vector3 baseLocal = gizmoTarget.localPosition;
        Vector3 baseWorld = gizmoTarget.position;

        Vector3 axis =
            moveAxis == Axis.X ? Vector3.right :
            moveAxis == Axis.Y ? Vector3.up : Vector3.forward;

        float dir = (direction == Direction.Positive) ? 1f : -1f;

        Vector3 start = useLocalSpace ? baseLocal : baseWorld;
        Vector3 end = start + axis * (moveAmount * dir);

        // แปลงเป็น world เมื่อวาดถ้าใช้ local
        Matrix4x4 m = useLocalSpace && gizmoTarget.parent ? gizmoTarget.parent.localToWorldMatrix : Matrix4x4.identity;
        Gizmos.matrix = m;

        Gizmos.color = Color.green; Gizmos.DrawLine(start, end);
        Gizmos.color = Color.cyan; Gizmos.DrawWireCube(start, Vector3.one * 0.2f);
        Gizmos.color = Color.red; Gizmos.DrawWireCube(end, Vector3.one * 0.2f);
    }
#endif
}
