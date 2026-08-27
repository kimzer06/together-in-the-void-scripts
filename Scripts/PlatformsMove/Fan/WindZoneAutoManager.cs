using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Manager ที่เปิดปิดลมอัตโนมัติตาม Pattern (เปิด X วินาที ปิด Y วินาที)
/// รองรับ FlyupZone, HeatDeathZone, PushZone และคอมโพเนนต์อื่นๆ ที่ implement IActivatable หรือ IWindModeActivatable
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[AddComponentMenu("WindZone/Auto Manager")]
public class WindZoneAutoManager : NetworkBehaviour
{
    #region Inspector Fields & Enums
    
    public enum PatternType
    {
        [Tooltip("เปิดปิดทั้งหมดพร้อมกัน (แบบเดิม)")]
        AllOnOff,
        
        [Tooltip("เปิดทีละตัวตามลำดับ (ตัวแรกเปิด → ปิด → ตัวที่สองเปิด → ปิด → ...)")]
        Sequential,
        
        [Tooltip("เปิดทีละตัวแต่ทับซ้อน (ตัวแรกเปิด → ตัวที่สองเปิด (ตัวแรกยังเปิด) → ตัวแรกปิด → ...)")]
        SequentialOverlap,
        
        [Tooltip("สลับกัน (ตัวที่ 1,3,5... เปิด → ตัวที่ 2,4,6... เปิด → สลับ)")]
        Alternating,
        
        [Tooltip("สุ่มเปิดปิด")]
        Random,
        
        [Tooltip("เปิดเป็นคลื่น (เปิดทีละตัวตามลำดับ แต่ไม่ปิดจนกว่าจะเปิดครบ)")]
        Wave
    }
    
    [Header("Target Zones")]
    [Tooltip("ลากคอมโพเนนต์ Wind Zone (FlyupZone, HeatDeathZone, PushZone) มาวางที่นี่")]
    [SerializeField] private List<MonoBehaviour> targetZones = new();
    
    [Header("Pattern Settings")]
    [Tooltip("ประเภท Pattern ที่จะใช้")]
    [SerializeField] private PatternType patternType = PatternType.AllOnOff;
    
    [Tooltip("เปิดใช้งาน Pattern อัตโนมัติ (ถ้าปิดจะต้องเปิดด้วย WindZoneTrigger)")]
    [SerializeField] private bool autoStart = false;
    
    [Tooltip("เวลาเปิดลม (วินาที) - ใช้กับ AllOnOff, Alternating")]
    [SerializeField] private float onDuration = 5f;
    
    [Tooltip("เวลาปิดลม (วินาที) - ใช้กับ AllOnOff, Alternating")]
    [SerializeField] private float offDuration = 3f;
    
    [Tooltip("เวลาที่แต่ละตัวเปิด (วินาที) - ใช้กับ Sequential, SequentialOverlap, Wave")]
    [SerializeField] private float perZoneOnDuration = 3f;
    
    [Tooltip("เวลาที่แต่ละตัวปิด (วินาที) - ใช้กับ Sequential, SequentialOverlap")]
    [SerializeField] private float perZoneOffDuration = 1f;
    
    [Tooltip("ดีเลย์ระหว่างการเปิดแต่ละตัว (วินาที) - ใช้กับ Sequential, SequentialOverlap, Wave")]
    [SerializeField] private float delayBetweenZones = 0.5f;
    
    [Tooltip("ดีเลย์ก่อนเริ่ม Pattern (วินาที)")]
    [SerializeField] private float startDelay = 0f;
    
    [Header("Random Pattern Settings")]
    [Tooltip("ความน่าจะเป็นที่จะเปิดแต่ละตัว (0-1) - ใช้กับ Random Pattern")]
    [SerializeField] private float randomActivateChance = 0.5f;
    
    [Tooltip("เวลาระหว่างการสุ่ม (วินาที) - ใช้กับ Random Pattern")]
    [SerializeField] private float randomCheckInterval = 1f;
    
    [Header("Wind Mode Settings")]
    [Tooltip("โหมดลมเมื่อเปิด (Push/Pull/Disabled)")]
    [SerializeField] private WindMode onMode = WindMode.Push;
    
    [Tooltip("โหมดลมเมื่อปิด (Push/Pull/Disabled)")]
    [SerializeField] private WindMode offMode = WindMode.Disabled;
    
    [Tooltip("ถ้าติ๊ก: ใช้ Activate(true/false) แทน SetWindMode (สำหรับคอมโพเนนต์ที่รองรับแค่ IActivatable)")]
    [SerializeField] private bool useActivateInstead = false;
    
    [Header("Initial State")]
    [Tooltip("สถานะเริ่มต้น (Server จะยึดค่านี้)")]
    [SerializeField] private bool startActive = false;
    
    [Header("Loop Settings")]
    [Tooltip("จำนวนรอบที่เล่น (0 = เล่นไม่สิ้นสุด)")]
    [SerializeField] private int loopCount = 0;
    
    [Tooltip("ถ้าติ๊ก: เมื่อเล่นครบรอบแล้วจะปิด Manager")]
    [SerializeField] private bool disableAfterLoops = false;
    
    [Header("Debug")]
    [SerializeField] private bool showDebugLogs = false;
    #endregion
    
    #region Runtime State
    private readonly NetworkVariable<bool> _isActiveNV = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _isRunningNV = new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private Coroutine _patternCoroutine;
    private int _currentLoopCount;
    private System.Random _random;
    #endregion
    
    #region Unity Lifecycle
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        if (IsServer)
        {
            _isActiveNV.Value = startActive;
            _isRunningNV.Value = false;
            _random = new System.Random(GetInstanceID() + (int)Time.time);
            
            // ตั้งสถานะเริ่มต้นให้กับ target zones
            if (patternType == PatternType.AllOnOff)
            {
                ApplyStateToTargets(startActive);
            }
            else
            {
                // สำหรับ Pattern อื่นๆ ให้ปิดทั้งหมดก่อน
                ApplyStateToAllTargets(false);
            }
            
            // ถ้า autoStart = true ให้เริ่ม Pattern ทันที
            if (autoStart)
            {
                StartPattern();
            }
        }
        
        _isActiveNV.OnValueChanged += OnActiveStateChanged;
        _isRunningNV.OnValueChanged += OnRunningStateChanged;
    }
    
    private void OnEnable()
    {
        _isActiveNV.OnValueChanged += OnActiveStateChanged;
        _isRunningNV.OnValueChanged += OnRunningStateChanged;
    }
    
    private void OnDisable()
    {
        _isActiveNV.OnValueChanged -= OnActiveStateChanged;
        _isRunningNV.OnValueChanged -= OnRunningStateChanged;
        
        // หยุด Coroutine เมื่อถูกปิด
        if (_patternCoroutine != null)
        {
            StopCoroutine(_patternCoroutine);
            _patternCoroutine = null;
        }
    }
    #endregion
    
    #region Network Events
    private void OnActiveStateChanged(bool prev, bool next)
    {
        // Client จะรับการเปลี่ยนแปลงสถานะและอัปเดต UI/Effects ผ่าน target zones
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] Active state changed: {next}");
        }
    }
    
    private void OnRunningStateChanged(bool prev, bool next)
    {
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] Running state changed: {next}");
        }
    }
    #endregion
    
    #region Public API
    /// <summary>
    /// เริ่ม Pattern อัตโนมัติ (เรียกจาก WindZoneTrigger หรือสคริปต์อื่น)
    /// </summary>
    public void StartPattern()
    {
        if (!IsServer) return;
        if (_isRunningNV.Value) return; // กำลังทำงานอยู่แล้ว
        
        _isRunningNV.Value = true;
        _currentLoopCount = 0;
        
        if (_patternCoroutine != null)
        {
            StopCoroutine(_patternCoroutine);
        }
        
        _patternCoroutine = StartCoroutine(PatternCoroutine());
    }
    
    /// <summary>
    /// หยุด Pattern อัตโนมัติ
    /// </summary>
    public void StopPattern()
    {
        if (!IsServer) return;
        
        _isRunningNV.Value = false;
        
        if (_patternCoroutine != null)
        {
            StopCoroutine(_patternCoroutine);
            _patternCoroutine = null;
        }
    }
    
    /// <summary>
    /// เปิด/ปิดลมทันที (ไม่รอ Pattern)
    /// </summary>
    public void SetActive(bool active)
    {
        if (!IsServer) return;
        
        _isActiveNV.Value = active;
        ApplyStateToTargets(active);
    }
    
    /// <summary>
    /// ตั้งค่าโหมดลมเมื่อเปิด/ปิด
    /// </summary>
    public void SetModes(WindMode onMode, WindMode offMode)
    {
        if (!IsServer) return;
        
        this.onMode = onMode;
        this.offMode = offMode;
    }
    
    /// <summary>
    /// ตั้งค่าเวลาเปิด/ปิด
    /// </summary>
    public void SetDurations(float onDuration, float offDuration)
    {
        if (!IsServer) return;
        
        this.onDuration = onDuration;
        this.offDuration = offDuration;
    }
    #endregion
    
    #region Core Logic
    private IEnumerator PatternCoroutine()
    {
        // รอดีเลย์ก่อนเริ่ม
        if (startDelay > 0f)
        {
            yield return new WaitForSeconds(startDelay);
        }
        
        // เริ่ม Pattern ตามประเภท
        while (_isRunningNV.Value)
        {
            switch (patternType)
            {
                case PatternType.AllOnOff:
                    yield return StartCoroutine(AllOnOffPattern());
                    break;
                    
                case PatternType.Sequential:
                    yield return StartCoroutine(SequentialPattern());
                    break;
                    
                case PatternType.SequentialOverlap:
                    yield return StartCoroutine(SequentialOverlapPattern());
                    break;
                    
                case PatternType.Alternating:
                    yield return StartCoroutine(AlternatingPattern());
                    break;
                    
                case PatternType.Random:
                    yield return StartCoroutine(RandomPattern());
                    break;
                    
                case PatternType.Wave:
                    yield return StartCoroutine(WavePattern());
                    break;
            }
            
            // ตรวจสอบจำนวนรอบ
            if (loopCount > 0)
            {
                _currentLoopCount++;
                if (_currentLoopCount >= loopCount)
                {
                    if (showDebugLogs)
                    {
                        Debug.Log($"[WindZoneAutoManager] Pattern completed {loopCount} loops");
                    }
                    
                    if (disableAfterLoops)
                    {
                        _isRunningNV.Value = false;
                        enabled = false;
                    }
                    break;
                }
            }
        }
        
        _patternCoroutine = null;
    }
    
    // Pattern: เปิดปิดทั้งหมดพร้อมกัน
    private IEnumerator AllOnOffPattern()
    {
        _isActiveNV.Value = true;
        ApplyStateToTargets(true);
        
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] All Zones ON (Mode: {onMode})");
        }
        
        yield return new WaitForSeconds(onDuration);
        
        _isActiveNV.Value = false;
        ApplyStateToTargets(false);
        
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] All Zones OFF (Mode: {offMode})");
        }
        
        yield return new WaitForSeconds(offDuration);
    }
    
    // Pattern: เปิดทีละตัวตามลำดับ (ตัวแรกเปิด → ปิด → ตัวที่สองเปิด → ปิด → ...)
    private IEnumerator SequentialPattern()
    {
        for (int i = 0; i < targetZones.Count && _isRunningNV.Value; i++)
        {
            if (targetZones[i] == null) continue;
            
            // เปิดตัวที่ i
            ApplyStateToSingleTarget(i, true);
            
            if (showDebugLogs)
            {
                Debug.Log($"[WindZoneAutoManager] Zone {i} ON (Mode: {onMode})");
            }
            
            yield return new WaitForSeconds(perZoneOnDuration);
            
            // ปิดตัวที่ i
            ApplyStateToSingleTarget(i, false);
            
            if (showDebugLogs)
            {
                Debug.Log($"[WindZoneAutoManager] Zone {i} OFF (Mode: {offMode})");
            }
            
            // ดีเลย์ก่อนเปิดตัวถัดไป
            if (i < targetZones.Count - 1)
            {
                yield return new WaitForSeconds(delayBetweenZones);
            }
        }
        
        // ดีเลย์ก่อนเริ่มรอบใหม่
        yield return new WaitForSeconds(offDuration);
    }
    
    // Pattern: เปิดทีละตัวแต่ทับซ้อน (ตัวแรกเปิด → ตัวที่สองเปิด (ตัวแรกยังเปิด) → ตัวแรกปิด → ...)
    private IEnumerator SequentialOverlapPattern()
    {
        for (int i = 0; i < targetZones.Count && _isRunningNV.Value; i++)
        {
            if (targetZones[i] == null) continue;
            
            // เปิดตัวที่ i
            ApplyStateToSingleTarget(i, true);
            
            if (showDebugLogs)
            {
                Debug.Log($"[WindZoneAutoManager] Zone {i} ON (Mode: {onMode})");
            }
            
            // ดีเลย์ก่อนเปิดตัวถัดไป
            if (i < targetZones.Count - 1)
            {
                yield return new WaitForSeconds(delayBetweenZones);
            }
            
            // ถ้าเปิดครบแล้ว ให้ปิดทีละตัว
            if (i == targetZones.Count - 1)
            {
                // ปิดทั้งหมดทีละตัว
                for (int j = 0; j < targetZones.Count && _isRunningNV.Value; j++)
                {
                    if (targetZones[j] == null) continue;
                    
                    ApplyStateToSingleTarget(j, false);
                    
                    if (showDebugLogs)
                    {
                        Debug.Log($"[WindZoneAutoManager] Zone {j} OFF (Mode: {offMode})");
                    }
                    
                    if (j < targetZones.Count - 1)
                    {
                        yield return new WaitForSeconds(perZoneOffDuration);
                    }
                }
            }
        }
        
        // ดีเลย์ก่อนเริ่มรอบใหม่
        yield return new WaitForSeconds(offDuration);
    }
    
    // Pattern: สลับกัน (ตัวที่ 1,3,5... เปิด → ตัวที่ 2,4,6... เปิด → สลับ)
    private IEnumerator AlternatingPattern()
    {
        // เปิดตัวคี่ (1, 3, 5, ...)
        for (int i = 0; i < targetZones.Count; i += 2)
        {
            if (targetZones[i] == null) continue;
            ApplyStateToSingleTarget(i, true);
        }
        
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] Odd Zones ON (Mode: {onMode})");
        }
        
        yield return new WaitForSeconds(onDuration);
        
        // ปิดตัวคี่
        for (int i = 0; i < targetZones.Count; i += 2)
        {
            if (targetZones[i] == null) continue;
            ApplyStateToSingleTarget(i, false);
        }
        
        // เปิดตัวคู่ (2, 4, 6, ...)
        for (int i = 1; i < targetZones.Count; i += 2)
        {
            if (targetZones[i] == null) continue;
            ApplyStateToSingleTarget(i, true);
        }
        
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] Even Zones ON (Mode: {onMode})");
        }
        
        yield return new WaitForSeconds(onDuration);
        
        // ปิดตัวคู่
        for (int i = 1; i < targetZones.Count; i += 2)
        {
            if (targetZones[i] == null) continue;
            ApplyStateToSingleTarget(i, false);
        }
        
        yield return new WaitForSeconds(offDuration);
    }
    
    // Pattern: สุ่มเปิดปิด
    private IEnumerator RandomPattern()
    {
        // สุ่มเปิดปิดแต่ละตัว
        for (int i = 0; i < targetZones.Count && _isRunningNV.Value; i++)
        {
            if (targetZones[i] == null) continue;
            
            bool shouldActivate = _random.NextDouble() < randomActivateChance;
            ApplyStateToSingleTarget(i, shouldActivate);
            
            if (showDebugLogs)
            {
                Debug.Log($"[WindZoneAutoManager] Zone {i} {(shouldActivate ? "ON" : "OFF")} (Random)");
            }
        }
        
        yield return new WaitForSeconds(randomCheckInterval);
        
        // ปิดทั้งหมด
        ApplyStateToAllTargets(false);
        
        yield return new WaitForSeconds(offDuration);
    }
    
    // Pattern: เปิดเป็นคลื่น (เปิดทีละตัวตามลำดับ แต่ไม่ปิดจนกว่าจะเปิดครบ)
    private IEnumerator WavePattern()
    {
        // เปิดทีละตัวตามลำดับ
        for (int i = 0; i < targetZones.Count && _isRunningNV.Value; i++)
        {
            if (targetZones[i] == null) continue;
            
            ApplyStateToSingleTarget(i, true);
            
            if (showDebugLogs)
            {
                Debug.Log($"[WindZoneAutoManager] Zone {i} ON (Wave)");
            }
            
            if (i < targetZones.Count - 1)
            {
                yield return new WaitForSeconds(delayBetweenZones);
            }
        }
        
        // รอให้เปิดครบทั้งหมด
        yield return new WaitForSeconds(perZoneOnDuration);
        
        // ปิดทั้งหมดพร้อมกัน
        ApplyStateToAllTargets(false);
        
        if (showDebugLogs)
        {
            Debug.Log($"[WindZoneAutoManager] All Zones OFF (Wave Complete)");
        }
        
        yield return new WaitForSeconds(offDuration);
    }
    
    private void ApplyStateToTargets(bool isActive)
    {
        WindMode targetMode = isActive ? onMode : offMode;
        
        foreach (MonoBehaviour zone in targetZones)
        {
            if (zone == null) continue;
            ApplyStateToZone(zone, isActive, targetMode);
        }
    }
    
    private void ApplyStateToAllTargets(bool isActive)
    {
        WindMode targetMode = isActive ? onMode : offMode;
        
        for (int i = 0; i < targetZones.Count; i++)
        {
            if (targetZones[i] == null) continue;
            ApplyStateToZone(targetZones[i], isActive, targetMode);
        }
    }
    
    private void ApplyStateToSingleTarget(int index, bool isActive)
    {
        if (index < 0 || index >= targetZones.Count) return;
        if (targetZones[index] == null) return;
        
        WindMode targetMode = isActive ? onMode : offMode;
        ApplyStateToZone(targetZones[index], isActive, targetMode);
    }
    
    private void ApplyStateToZone(MonoBehaviour zone, bool isActive, WindMode targetMode)
    {
        if (zone == null) return;
        
        if (useActivateInstead)
        {
            // ใช้ Activate สำหรับคอมโพเนนต์ที่รองรับแค่ IActivatable
            if (zone is IActivatable activatable)
            {
                activatable.Activate(isActive);
            }
            else
            {
                Debug.LogWarning($"[WindZoneAutoManager] {zone.GetType().Name} ไม่ได้ implement IActivatable", this);
            }
        }
        else
        {
            // ใช้ SetWindMode สำหรับคอมโพเนนต์ที่รองรับ IWindModeActivatable
            if (zone is IWindModeActivatable windActivatable)
            {
                windActivatable.SetWindMode(targetMode);
            }
            else if (zone is IActivatable activatable)
            {
                // Fallback: ใช้ Activate ถ้าไม่มี IWindModeActivatable
                activatable.Activate(isActive);
            }
            else
            {
                Debug.LogWarning($"[WindZoneAutoManager] {zone.GetType().Name} ไม่ได้ implement IActivatable หรือ IWindModeActivatable", this);
            }
        }
    }
    #endregion
    
    #region Properties
    public bool IsActive => _isActiveNV.Value;
    public bool IsRunning => _isRunningNV.Value;
    #endregion
}

