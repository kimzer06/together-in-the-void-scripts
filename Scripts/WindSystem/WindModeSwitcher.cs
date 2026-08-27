using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// ผู้จัดการสวิตช์ลมแบบ 2 โหมด (A/B Toggle) - (เวอร์ชันอัปเกรด V2)
/// - รับ MonoBehaviour ที่มี IWindModeActivatable (เช่น FlyupZone, RotatingPlatform) มาใส่ใน List
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WindModeSwitcher : NetworkBehaviour, ISwitchableWindManager
{
    [Header("Master Lists (เรียง Index ให้ตรงกัน)")]
    [Tooltip("ลาก GameObject ที่มี 'FlyupZone' หรือ MonoBehaviour ที่มี SetWindMode มาลากใส่ที่นี่")]
    [SerializeField] private List<MonoBehaviour> fans = new();

    [Tooltip("ลาก GameObject ที่มี 'RotatingPlatform' หรือ MonoBehaviour ที่มี SetWindMode มาลากใส่ที่นี่")]
    [SerializeField] private List<MonoBehaviour> receivers = new();

    [Header("Mode A Settings")]
    [Tooltip("กำหนดโหมด (Push/Pull/Disabled) ของคู่ Fans/Receivers แต่ละตัว เมื่อ 'Mode A' ทำงาน")]
    [SerializeField] private List<WindMode> modeASettings = new();

    [Header("Mode B Settings")]
    [Tooltip("กำหนดโหมด (Push/Pull/Disabled) ของคู่ Fans/Receivers แต่ละตัว เมื่อ 'Mode B' ทำงาน")]
    [SerializeField] private List<WindMode> modeBSettings = new();

    [Header("Config")]
    [Tooltip("สถานะเริ่มต้นเมื่อเกิด (Server จะยึดค่านี้)")]
    [SerializeField] private bool startInModeA = true;
    
    [Tooltip("ดีเลย์ก่อนที่จะสลับโหมดจริง (หลังได้รับสัญญาณจากสวิตช์)")]
    [SerializeField] private float activationDelay = 0.2f;

    [Header("Visuals (Optional)")]
    [Tooltip("Indicator ที่จะแสดงสถานะ A (Push) หรือ B (Pull)")]
    [SerializeField] private WindStateIndicator globalIndicator;

    private NetworkVariable<bool> _isModeA_NV =
        new NetworkVariable<bool>(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // --- Lifecycle ---

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer)
        {
            _isModeA_NV.Value = startInModeA;
        }

        _isModeA_NV.OnValueChanged += OnStateChanged;
        ApplyState(_isModeA_NV.Value);
        // Re-apply once more on the next frame to ensure dependent components (e.g. RotatingPlatform)
        // have completed their initialization before receiving SetWindMode.
        StartCoroutine(ApplyInitialStateNextFrame_Co());
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        _isModeA_NV.OnValueChanged -= OnStateChanged;
    }

    private void OnStateChanged(bool previousValue, bool newValue)
    {
        ApplyState(newValue);
    }

    private IEnumerator ApplyInitialStateNextFrame_Co()
    {
        yield return null; // wait one frame
        ApplyState(_isModeA_NV.Value);
    }

    // --- Public API for Switch (ISwitchableWindManager) ---

    public void Server_OnSwitchPressed(float extraDelay)
    {
        if (!IsServer) return;
        
        float finalDelay = Mathf.Max(activationDelay, extraDelay);
        StartCoroutine(ToggleModeAfterDelay_Co(finalDelay));
    }

    private IEnumerator ToggleModeAfterDelay_Co(float delay)
    {
        if (delay > 0.01f)
        {
            yield return new WaitForSeconds(delay);
        }
        
        _isModeA_NV.Value = !_isModeA_NV.Value;
    }

    // --- Core Logic ---

    private void ApplyState(bool isModeA)
    {
        List<WindMode> settingsToApply = isModeA ? modeASettings : modeBSettings;

        if (globalIndicator != null)
        {
            globalIndicator.SetWindMode(isModeA ? WindMode.Push : WindMode.Pull);
        }

        // วนลูปตามจำนวน "พัดลม" (Fans)
        for (int i = 0; i < fans.Count; i++)
        {
            if (i >= settingsToApply.Count)
            {
                Debug.LogWarning($"[WindModeSwitcher] ไม่ได้ตั้งค่า Mode {(isModeA ? 'A' : 'B')} ให้กับ Index {i}", this);
                continue;
            }

            WindMode targetMode = settingsToApply[i];

            // สั่งการ "พัดลม" (Fan) ที่ index i
            SetComponentMode(fans[i], targetMode);

            // สั่งการ "ตัวรับสัญญาณ" (Receiver) ที่ index i (ถ้ามี)
            if (i < receivers.Count)
            {
                SetComponentMode(receivers[i], targetMode);
            }
        }
    }

    /// <summary>
    /// ตรวจสอบว่า MonoBehaviour เป็น IWindModeActivatable หรือไม่ แล้วเรียก SetWindMode
    /// </summary>
    private void SetComponentMode(MonoBehaviour component, WindMode mode)
    {
        if (component == null) return;

        if (component is IWindModeActivatable windActivatable)
        {
            windActivatable.SetWindMode(mode);
        }
        else
        {
            Debug.LogWarning($"[WindModeSwitcher] {component.name} ไม่ได้ implement IWindModeActivatable", this);
        }
    }

#if UNITY_EDITOR
    /// <summary>
    /// (EDITOR-ONLY) ช่วยปรับขนาด List Setting ให้อัตโนมัติใน Inspector
    /// </summary>
    private void OnValidate()
    {
        // ปรับขนาด list setting (A/B) ให้เท่ากับ list "Fans"
        AdjustListSize(modeASettings, fans.Count, WindMode.Disabled);
        AdjustListSize(modeBSettings, fans.Count, WindMode.Disabled);
        
        // ปรับขนาด list "Receivers" ให้เท่ากับ list "Fans" (ใส่ค่า null)
        AdjustListSize(receivers, fans.Count, null);
    }

    // (Helper Function ของ OnValidate - ไม่ต้องแก้ไข)
    private void AdjustListSize<T>(List<T> list, int targetCount, T defaultValue)
    {
        if (list == null) list = new List<T>();
        
        while (list.Count < targetCount)
        {
            list.Add(defaultValue);
        }
        while (list.Count > targetCount)
        {
            list.RemoveAt(list.Count - 1);
        }
    }
#endif
}