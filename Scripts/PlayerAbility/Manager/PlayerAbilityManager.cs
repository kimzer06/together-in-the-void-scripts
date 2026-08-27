using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;

// [System.Serializable] เพื่อให้เราแก้ไขได้ใน Inspector
[System.Serializable]
public struct AbilityEntry
{
    // ID ที่เราจะใช้เรียกจาก TriggerZone (เช่น "Dash", "Shoot", "Grapple")
    public string abilityID; 
    
    // สคริปต์ หรือ Component ที่เราจะควบคุม (เช่น DashSkill.cs, ParticleSystem)
    public Behaviour targetComponent; 
}

public class PlayerAbilityManager : NetworkBehaviour
{
    // [1] ลิสต์ของสกิลทั้งหมดที่ Player นี้มี
    [SerializeField]
    private List<AbilityEntry> allAbilities;

    // [2] ตัวแปร Network ที่เก็บสถานะ On/Off ของสกิลใน allAbilities
    private NetworkList<bool> abilityStates;

    private void Awake()
    {
        // สร้าง NetworkList และกำหนดสิทธิ์: Server เขียน, ทุกคนอ่าน
        abilityStates = new NetworkList<bool>(
            null,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server
        );
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // [3] (Server-Only) เมื่อเกิดมาครั้งแรก
        if (IsServer)
        {
            // 3a. ตั้งค่าเริ่มต้นให้ NetworkList (จากค่า enabled ใน Prefab)
            InitializeAbilityStates(); 
            
            // 3b. ค้นหา "กฎของซีน" (SceneAbilityRule) แล้วบังคับใช้ทันที
            ApplySceneWideRules();
        }

        // [4] ติดตามการเปลี่ยนแปลงใน List นี้ (Client และ Host จะรันส่วนนี้)
        abilityStates.OnListChanged += OnAbilityStateChanged;

        // [5] ใช้สถานะปัจจุบันกับทุกสกิลทันทีที่เกิด (สำคัญสำหรับ Late-join)
        ApplyAllStates();
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        if (abilityStates != null)
        {
            abilityStates.OnListChanged -= OnAbilityStateChanged;
        }
    }

    // [Server-Only] อ่านค่าเริ่มต้นจาก Prefab แล้วยัดใส่ NetworkList
    private void InitializeAbilityStates()
    {
        abilityStates.Clear();
        foreach (var ability in allAbilities)
        {
            if (ability.targetComponent != null)
            {
                abilityStates.Add(ability.targetComponent.enabled);
            }
            else
            {
                abilityStates.Add(false); 
                Debug.LogWarning($"Ability '{ability.abilityID}' on {gameObject.name} has no Target Component!");
            }
        }
    }

    // [Server-Only] ค้นหาและบังคับใช้กฎของซีน
    private void ApplySceneWideRules()
    {
        SceneAbilityRule rule = FindObjectOfType<SceneAbilityRule>();

        if (rule != null)
        {
            Debug.Log($"[Server Player {OwnerClientId}] Applying Scene-Wide Rules...");
            Server_SetAbilityStates(rule.GetIDsToEnable(), rule.GetIDsToDisable());
        }
    }

    // [All Clients] เมื่อ Server แก้ไขค่าใน NetworkList... ฟังก์ชันนี้จะรัน
    private void OnAbilityStateChanged(NetworkListEvent<bool> changeEvent)
    {
        ApplyState(changeEvent.Index, changeEvent.Value);
    }

    // [All Clients] ฟังก์ชันสำหรับใช้สถานะเริ่มต้น (ตอนเกิด)
    private void ApplyAllStates()
    {
        for (int i = 0; i < abilityStates.Count && i < allAbilities.Count; i++)
        {
            ApplyState(i, abilityStates[i]);
        }
    }

    // [All Clients] ฟังก์ชันหลักในการเปิด/ปิด Component
    private void ApplyState(int index, bool state)
    {
        if (index >= allAbilities.Count || allAbilities[index].targetComponent == null)
        {
            return; 
        }
        
        allAbilities[index].targetComponent.enabled = state;
    }

    /// <summary>
    /// สกิลนี้ถูก Manager + NetworkList เปิดอยู่หรือไม่ (รวมกฎ SceneAbilityRule).
    /// สคริปต์สกิลไม่ควรบังคับ enabled = true ทับเมื่อค่านี้เป็น false
    /// </summary>
    public bool IsAbilityBehaviourAllowed(Behaviour behaviour)
    {
        if (behaviour == null || allAbilities == null || abilityStates == null)
            return true;

        for (int i = 0; i < allAbilities.Count; i++)
        {
            if (allAbilities[i].targetComponent != behaviour)
                continue;
            if (i >= abilityStates.Count)
                return true;
            return abilityStates[i];
        }

        return true;
    }

    // [Public API] [Server-Only]
    public void Server_SetAbilityStates(List<string> idsToEnable, List<string> idsToDisable)
    {
        if (!IsServer)
        {
            Debug.LogWarning("Server_SetAbilityStates was called from a client. Ignoring.");
            return;
        }

        for (int i = 0; i < allAbilities.Count; i++)
        {
            string currentID = allAbilities[i].abilityID;

            if (idsToEnable.Contains(currentID))
            {
                abilityStates[i] = true; 
            }
            else if (idsToDisable.Contains(currentID))
            {
                abilityStates[i] = false;
            }
        }
    }

    // -----------------------------------------------------------------
    // !! [ฟังก์ชันใหม่ที่เพิ่มเข้ามา] !!
    // -----------------------------------------------------------------
    // นี่คือฟังก์ชันสาธารณะที่สคริปต์อื่น (เช่น PlayerDeath)
    // จะเรียกใช้ "บน Server" เมื่อผู้เล่นเกิดใหม่ (Respawn)
    public void Server_ReapplyAllRulesOnRespawn()
    {
        if (!IsServer) return;

        Debug.Log($"[Server Player {OwnerClientId}] Re-applying all rules on respawn.");

        // ขั้นที่ 1: รีเซ็ตสถานะสกิลทั้งหมดกลับไปเป็นค่าเริ่มต้น
        // ใช้วิธี update in-place เพื่อหลีกเลี่ยง Clear() ที่อาจทำให้เกิดปัญหา sync กับ Client
        ResetAllAbilitiesToDefault();

        // ขั้นที่ 2: บังคับใช้ "กฎของซีน" (SceneAbilityRule) ทับลงไป
        // (ถ้าซีนนี้ห้ามยิง สกิลยิงก็จะถูกปิดตรงนี้ทันที)
        ApplySceneWideRules();
    }

    // -----------------------------------------------------------------
    // [Server-Only] Reset abilities โดยไม่ Clear NetworkList
    // ใช้วิธี update in-place เพื่อหลีกเลี่ยงปัญหา sync
    // -----------------------------------------------------------------
    private void ResetAllAbilitiesToDefault()
    {
        for (int i = 0; i < allAbilities.Count && i < abilityStates.Count; i++)
        {
            // Reset เป็น true (enabled) เป็นค่าเริ่มต้น
            // SceneAbilityRule จะปิดสกิลที่ต้องปิดในขั้นตอนถัดไป
            if (abilityStates[i] != true)
            {
                abilityStates[i] = true;
            }
        }
    }
}