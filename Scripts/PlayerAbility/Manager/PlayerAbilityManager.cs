using UnityEngine;
using Fusion;
using System.Collections.Generic;

// [System.Serializable] เพื่อให้เราแก้ไขได้ใน Inspector
[System.Serializable]
public struct AbilityEntry
{
    // ID ที่เราจะใช้เรียกจาก TriggerZone (เช่น "Dash", "Shoot", "Grapple")
    public string abilityID;

    // สคริปต์ หรือ Component ที่เราจะควบคุม (เช่น DashSkill.cs, ParticleSystem)
    public UnityEngine.Behaviour targetComponent;
}

public class PlayerAbilityManager : NetworkBehaviour
{
    // bitmask ใช้ได้สูงสุด 32 สกิลต่อผู้เล่น
    private const int MaxAbilities = 32;

    // [1] ลิสต์ของสกิลทั้งหมดที่ Player นี้มี
    [SerializeField]
    private List<AbilityEntry> allAbilities;

    // [2] bit i = สถานะ On/Off ของ allAbilities[i] (เดิม NetworkList<bool>)
    // Shared Mode: เจ้าของตัวละคร (state authority) เป็นคนเขียน ทุกเครื่องอ่านแล้ว apply ผ่าน OnChangedRender
    [Networked, OnChangedRender(nameof(OnAbilityMaskChanged))]
    private int AbilityMask { get; set; }

    private bool IsSpawned => Object != null && Object.IsValid;

    public override void Spawned()
    {
        if (allAbilities != null && allAbilities.Count > MaxAbilities)
            Debug.LogError($"[PlayerAbilityManager] {name} มีสกิล {allAbilities.Count} ตัว เกิน {MaxAbilities} — ตัวที่เกินจะไม่ถูกซิงค์");

        // [3] (เจ้าของเท่านั้น) เมื่อเกิดมาครั้งแรก
        if (HasStateAuthority)
        {
            // 3a. ตั้งค่าเริ่มต้นจากค่า enabled ใน Prefab
            InitializeAbilityStates();

            // 3b. ค้นหา "กฎของซีน" (SceneAbilityRule) แล้วบังคับใช้ทันที
            ApplySceneWideRules();
        }

        // [4] ใช้สถานะปัจจุบันกับทุกสกิลทันทีที่เกิด (สำคัญสำหรับ Late-join)
        ApplyAllStates();
    }

    // [ทุกเครื่อง] เมื่อเจ้าของแก้ไขค่า... ฟังก์ชันนี้จะรัน
    private void OnAbilityMaskChanged() => ApplyAllStates();

    // [เจ้าของเท่านั้น] อ่านค่าเริ่มต้นจาก Prefab
    private void InitializeAbilityStates()
    {
        int mask = 0;
        for (int i = 0; i < allAbilities.Count && i < MaxAbilities; i++)
        {
            var target = allAbilities[i].targetComponent;
            if (target == null)
            {
                Debug.LogWarning($"Ability '{allAbilities[i].abilityID}' on {gameObject.name} has no Target Component!");
                continue;
            }
            if (target.enabled) mask |= 1 << i;
        }
        AbilityMask = mask;
    }

    // [เจ้าของเท่านั้น] ค้นหาและบังคับใช้กฎของซีน
    private void ApplySceneWideRules()
    {
        var rule = FindFirstObjectByType<SceneAbilityRule>();

        if (rule != null)
        {
            Debug.Log($"[PlayerAbilityManager] {name}: Applying Scene-Wide Rules...");
            SetAbilityStates(rule.GetIDsToEnable(), rule.GetIDsToDisable());
        }
    }

    // [ทุกเครื่อง] ใช้สถานะจาก mask กับทุก Component
    private void ApplyAllStates()
    {
        if (!IsSpawned || allAbilities == null) return;

        int mask = AbilityMask;
        for (int i = 0; i < allAbilities.Count && i < MaxAbilities; i++)
        {
            ApplyState(i, (mask & (1 << i)) != 0);
        }
    }

    // [ทุกเครื่อง] ฟังก์ชันหลักในการเปิด/ปิด Component
    private void ApplyState(int index, bool state)
    {
        if (index >= allAbilities.Count || allAbilities[index].targetComponent == null)
        {
            return;
        }

        allAbilities[index].targetComponent.enabled = state;
    }

    /// <summary>
    /// สกิลนี้ถูก Manager เปิดอยู่หรือไม่ (รวมกฎ SceneAbilityRule).
    /// สคริปต์สกิลไม่ควรบังคับ enabled = true ทับเมื่อค่านี้เป็น false
    /// </summary>
    public bool IsAbilityBehaviourAllowed(UnityEngine.Behaviour behaviour)
    {
        if (behaviour == null || allAbilities == null || !IsSpawned)
            return true;

        for (int i = 0; i < allAbilities.Count; i++)
        {
            if (allAbilities[i].targetComponent != behaviour)
                continue;
            if (i >= MaxAbilities)
                return true;
            return (AbilityMask & (1 << i)) != 0;
        }

        return true;
    }

    /// <summary>
    /// เปิด/ปิดสกิลตาม ID (ถ้าอยู่ในทั้งสองลิสต์ ฝั่ง enable ชนะ — เหมือนเดิม)
    /// เรียกได้จากทุกเครื่อง: เจ้าของเขียนตรง คนอื่นส่ง RPC ไปหาเจ้าของ
    /// </summary>
    public void SetAbilityStates(List<string> idsToEnable, List<string> idsToDisable)
    {
        if (!IsSpawned || allAbilities == null) return;

        int enableMask = 0;
        int disableMask = 0;
        for (int i = 0; i < allAbilities.Count && i < MaxAbilities; i++)
        {
            string currentID = allAbilities[i].abilityID;

            if (idsToEnable != null && idsToEnable.Contains(currentID))
                enableMask |= 1 << i;
            else if (idsToDisable != null && idsToDisable.Contains(currentID))
                disableMask |= 1 << i;
        }

        if (HasStateAuthority) ApplyMasks(enableMask, disableMask);
        else RPC_SetAbilityStates(enableMask, disableMask);
    }

    [Rpc(RpcSources.All, RpcTargets.StateAuthority, TickAligned = false)]
    private void RPC_SetAbilityStates(int enableMask, int disableMask) => ApplyMasks(enableMask, disableMask);

    private void ApplyMasks(int enableMask, int disableMask)
    {
        AbilityMask = (AbilityMask | enableMask) & ~disableMask;
        // เครื่องเจ้าของ apply ทันที ไม่ต้องรอ OnChangedRender รอบ Render ถัดไป
        ApplyAllStates();
    }

    /// <summary>
    /// [เจ้าของเท่านั้น] เรียกตอนเกิดใหม่ (Respawn) จาก PlayerDeath:
    /// รีเซ็ตทุกสกิลเป็น "เปิด" แล้วให้กฎของซีนปิดตัวที่ต้องปิดทับลงไป
    /// </summary>
    public void ReapplyAllRulesOnRespawn()
    {
        if (!IsSpawned || !HasStateAuthority || allAbilities == null) return;

        Debug.Log($"[PlayerAbilityManager] {name}: Re-applying all rules on respawn.");

        // ขั้นที่ 1: รีเซ็ตสถานะสกิลทั้งหมดเป็นเปิด (SceneAbilityRule จะปิดตัวที่ต้องปิดในขั้นถัดไป)
        int count = Mathf.Min(allAbilities.Count, MaxAbilities);
        AbilityMask = count >= MaxAbilities ? -1 : (1 << count) - 1;

        // ขั้นที่ 2: บังคับใช้ "กฎของซีน" (SceneAbilityRule) ทับลงไป
        ApplySceneWideRules();
        ApplyAllStates();
    }
}
