using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class CentralSwitchManager : NetworkBehaviour
{
    [Serializable] public class TargetEntry
    {
        [Tooltip("คอมโพเนนต์ที่ implement IActivatable")]
        public MonoBehaviour activatableComponent;
        public bool allow = true;
        public bool invert = false;
    }

    [Serializable] public class GroupConfig
    {
        // ... (GroupConfig เดิม)
        [Tooltip("รหัสกลุ่มที่สวิตช์ต้องตั้งให้ตรงกัน")]
        public int groupId = 0;

        [Min(1)] public int requiredSwitchCount = 2;

        [Tooltip("เวลาหน้าต่าง (วินาที) ที่ต้องกดให้ทันกัน; ถ้า <= 0 = ไม่จำกัดเวลา")]
        public float pressWindowSeconds = 1.0f;

        [Tooltip("ต้องเป็นผู้เล่นคนละคนกันหรือไม่")]
        public bool requireDistinctPlayers = true;

        [Header("Targets (สั่งเมื่อสำเร็จ)")]
        public List<TargetEntry> targets = new();

        [Header("Behavior")]
        [Tooltip("ล็อกกลุ่มหลังสำเร็จ (ไม่ให้ทำซ้ำ)")]
        public bool lockAfterSuccess = true;

        [Tooltip("สลับสถานะทุกครั้งที่สำเร็จ (toggle ทั้งกลุ่ม)")]
        public bool toggleOnSuccess = false;

        [Tooltip("ดีเลย์เพิ่มก่อนสั่งเป้าหมาย (ซิงค์ให้คันโยกเล่นจบก่อน)")]
        public float extraDelayBeforeActivate = 0f;

        [Header("Particles (เล่นเมื่อสำเร็จ)")]
        [Tooltip("ParticleSystem ที่วางไว้ในแมป — จะเล่นเมื่อกลุ่มนี้ทำงานสำเร็จ")]
        public List<ParticleSystem> particles = new();
    }

    // ---------------- Player Locking Options ----------------
    [Header("=== Player Locking (ในเครื่องของผู้เล่น) ===")]
    [Tooltip("ชื่อคลาสของสคริปต์ที่ต้องการ 'ปิด' ตอนล็อก (เช่น ThirdPersonController_Rigidbody, PlayerMovement, ฯลฯ)")]
    [SerializeField] private List<string> disableBehaviourTypeNames = new();

    [Tooltip("ถ้ามี Rigidbody บน Player: ดับความเร็วเมื่อถูกล็อก")]
    [SerializeField] private bool zeroVelocityOnLock = true;

    [Tooltip("ถ้ามี Rigidbody บน Player: Freeze Position (ปล่อยให้หมุน Y ได้ เพื่อให้ผู้เล่นยังเหลียวกล้อง/หมุนตัวได้)")]
    [SerializeField] private bool freezePositionOnLock = true;

    // ---------------- Runtime State ----------------
    private class GroupState
    {
        public float firstPressAt = -999f;
        public readonly HashSet<InteractSwitch> switchesPressed = new();
        public readonly HashSet<ulong> clientsPressed = new();

        // ⭐ การแก้ไข 1: กู้คืน Dictionary สำหรับเก็บผู้กดสวิตช์แต่ละตัว (จำเป็นสำหรับการยกเลิก)
        public readonly Dictionary<InteractSwitch, ulong> pressedBy = new(); 

        public bool locked = false;
        public bool toggleState = false;
    }

    [Header("Groups")]
    public List<GroupConfig> groups = new();

    private readonly Dictionary<int, GroupState> _gstates = new();

    private GroupConfig GetGroup(int id) => groups.Find(g => g.groupId == id);

    private GroupState GetOrCreateState(int id)
    {
        if (!_gstates.TryGetValue(id, out var st))
        {
            st = new GroupState();
            _gstates[id] = st;
        }
        return st;
    }

    // ======================================================================
    // ===================== Player Locking (Core) ===========================
    // ======================================================================

    // NOTE: คงไว้เป็น static เพื่อให้ LockOwnerClientRpc ที่รันบน Local Client แต่ละเครื่อง
    // สามารถจัดการสถานะ Rigidbody ของ Local Player ได้อย่างอิสระ
    private static RigidbodyConstraints _rbConstraintsBeforeLock;
    private static bool _savedConstraints = false;
    private static int _lockDepth = 0; 

    // helper: พยายามล็อกเฉพาะ movement ถ้า component รองรับ
    private static bool TrySetMovementLocked(Behaviour b, bool locked)
    {
        var t = b.GetType();
        var m = t.GetMethod("SetMovementLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (m != null)
        {
            var ps = m.GetParameters();
            if (ps.Length == 1 && ps[0].ParameterType == typeof(bool))
            {
                m.Invoke(b, new object[] { locked });
                return true;
            }
        }
        var p = t.GetProperty("MovementLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanWrite && p.PropertyType == typeof(bool))
        {
            p.SetValue(b, locked);
            return true;
        }
        var f = t.GetField("MovementLocked", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null && f.FieldType == typeof(bool))
        {
            f.SetValue(b, locked);
            return true;
        }
        return false;
    }

    // Server → สั่งล็อก/ปลดล็อกเครื่อง clientId เป้าหมาย
    private void LockClient(ulong clientId, bool locked)
    {
        if (!IsServer || NetworkManager.Singleton == null) return;
        var send = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
        };
        LockOwnerClientRpc(locked, send);
    }

    // RPC นี้ทำงาน “เฉพาะ” ไคลเอนต์ที่เป็นเจ้าของตาม target ที่ส่งมา
    [ClientRpc]
    private void LockOwnerClientRpc(bool locked, ClientRpcParams sendTo)
    {
        try
        {
            var nm = NetworkManager.Singleton;
            var playerObj = nm?.LocalClient?.PlayerObject;
            if (playerObj == null) return;

            var go = playerObj.gameObject;
            
            // ... (โค้ด Locking/Unlocking Movement Scripts และ Rigidbody เหมือนเดิม)
            if (disableBehaviourTypeNames != null && disableBehaviourTypeNames.Count > 0)
            {
                foreach (var typeName in disableBehaviourTypeNames)
                {
                    if (string.IsNullOrWhiteSpace(typeName)) continue;
                    foreach (var b in FindBehavioursByTypeName(go, typeName))
                    {
                        if (b == null) continue;

                        if (!TrySetMovementLocked(b, locked))
                        {
                            b.enabled = !locked;
                        }
                    }
                }
            }

            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
#if UNITY_6000_0_OR_NEWER
                if (locked && zeroVelocityOnLock) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
#else
                if (locked && zeroVelocityOnLock) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
#endif
                if (freezePositionOnLock)
                {
                    if (locked)
                    {
                        if (_lockDepth == 0 && !_savedConstraints)
                        {
                            _rbConstraintsBeforeLock = rb.constraints;
                            _savedConstraints = true;
                        }
                        _lockDepth++;

                        rb.constraints = _rbConstraintsBeforeLock
                                       | RigidbodyConstraints.FreezePosition; 
                    }
                    else
                    {
                        _lockDepth = Mathf.Max(0, _lockDepth - 1);

                        if (_lockDepth == 0)
                        {
                            if (_savedConstraints)
                            {
                                rb.constraints = _rbConstraintsBeforeLock;
                            }
                            else
                            {
                                rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
                            }
                            _savedConstraints = false;
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[CentralSwitchManager] LockOwnerClientRpc error: {e.Message}");
        }
    }

    // ค้นหา Behaviour ใน Player ตามชื่อคลาส (รองรับ simple name) (โค้ดเดิม)
    private static IEnumerable<Behaviour> FindBehavioursByTypeName(GameObject root, string simpleTypeName)
    {
        var t = Type.GetType(simpleTypeName, throwOnError: false);
        if (t == null)
        {
            t = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => SafeGetTypeByName(a, simpleTypeName))
                .FirstOrDefault(x => x != null);
        }

        if (t == null || !typeof(Behaviour).IsAssignableFrom(t))
        {
            foreach (var b in root.GetComponentsInChildren<Behaviour>(true))
                if (b != null && b.GetType().Name == simpleTypeName)
                    yield return b;
            yield break;
        }

        foreach (var c in root.GetComponentsInChildren(t, true))
            if (c is Behaviour b) yield return b;
    }

    private static Type SafeGetTypeByName(Assembly asm, string simpleTypeName)
    {
        try { return asm.GetTypes().FirstOrDefault(tp => tp.Name == simpleTypeName); }
        catch { return null; }
    }

    // ======================================================================
    // ===================== Flow จาก InteractSwitch =========================
    // ======================================================================

    /// <summary>
    /// เรียกจาก InteractSwitch (ฝั่ง Server) เมื่อผู้เล่นกดผ่านเงื่อนไขเบื้องต้นแล้ว
    /// **เพิ่ม Logic: ตรวจสอบการยกเลิก (กดซ้ำ)**
    /// </summary>
    public void Server_OnSwitchPressed(
        InteractSwitch sw,
        int groupId,
        ulong senderClientId,
        bool nextStateSuggestedBySwitch,
        float switchActivationDelay,
        InteractSwitch.PressMode switchPressMode,
        bool switchDisableAfterFire)
    {
        if (!IsServer) return;

        var cfg = GetGroup(groupId);
        if (cfg == null) return;

        var st = GetOrCreateState(groupId);
        if (st.locked) return;

        // ⭐ การแก้ไข 2: ตรวจสอบการยกเลิก (Cancel)
        if (st.clientsPressed.Contains(senderClientId))
        {
            // ผู้เล่นคนนี้เคยกดแล้ว: ทำการยกเลิก (Un-join)

            // 1. ปลดล็อกผู้เล่น
            LockClient(senderClientId, false);

            // 2. ลบสวิตช์ทั้งหมดที่ client นี้เคยกด
            var toRemove = new List<InteractSwitch>();
            // ⭐ ต้องวนลูปผ่าน pressedBy เพราะผู้เล่นคนเดียวอาจกดสวิตช์หลายตัว
            foreach (var kv in st.pressedBy)
                if (kv.Value == senderClientId) toRemove.Add(kv.Key);

            foreach (var s in toRemove)
            {
                st.pressedBy.Remove(s);
                st.switchesPressed.Remove(s);
            }

            st.clientsPressed.Remove(senderClientId);

            // 3. รีเซ็ต firstPressAt ถ้าไม่มีใครกดแล้ว
            if (st.switchesPressed.Count == 0)
                st.firstPressAt = -999f;
            
            // 4. สั่งให้สวิตช์ที่ถูกยกเลิกกลับสถานะ (ถ้าจำเป็น - ต้องเขียนใน InteractSwitch)
            // แต่สำหรับตอนนี้เราแค่ return เพื่อหยุด flow
            foreach (var s in toRemove)
            {
                s.Server_SetHolder(ulong.MaxValue); // Clear holder
                s.ResetLeverNetwork();               // หมุนคันโยกกลับตำแหน่งเดิม
            }

            return; // หยุดที่นี่: การทำงานจบลงด้วยการยกเลิก
        }


        // ------------------ JOIN Logic (โค้ดเดิม) ------------------

        float now = Time.time;
        bool unlimited = cfg.pressWindowSeconds <= 0f;

        // กรณี "จำกัดเวลา": เริ่ม/รีสตาร์ทหน้าต่างเมื่อหมดเวลา
        if (!unlimited)
        {
            if (st.firstPressAt < 0f || now - st.firstPressAt > cfg.pressWindowSeconds)
            {
                // ปลดล็อกคนค้างจากรอบก่อน (ถ้ามี)
                foreach (var cid in st.clientsPressed)
                    LockClient(cid, false);

                st.firstPressAt = now;
                st.switchesPressed.Clear();
                st.clientsPressed.Clear();
                
                // Clear holders before clearing dictionary
                foreach (var kv in st.pressedBy)
                {
                    kv.Key.Server_SetHolder(ulong.MaxValue);
                    kv.Key.ResetLeverNetwork(); // หมุนคันโยกกลับตอน timeout
                }
                st.pressedBy.Clear(); // ⭐ เคลียร์ pressedBy ด้วย
            }
        }
        else
        {
            // ไม่จำกัดเวลา: เปิดหน้าต่างครั้งแรกแล้วสะสมต่อไปเรื่อยๆ
            if (st.firstPressAt < 0f)
                st.firstPressAt = now;
        }

        // สะสมสวิตช์ที่ถูกกด และ client ที่กด
        st.switchesPressed.Add(sw);
        st.clientsPressed.Add(senderClientId);
        st.pressedBy[sw] = senderClientId; // ⭐ การแก้ไข 3: บันทึกว่าใครกดสวิตช์ตัวไหน
        sw.Server_SetHolder(senderClientId); // Set holder on switch

        // ล็อกผู้กดรายนี้ไว้รอเพื่อน
        LockClient(senderClientId, true);

        // ตรวจเงื่อนไขครบ
        bool enoughSwitches = st.switchesPressed.Count >= cfg.requiredSwitchCount;
        bool distinctOK = !cfg.requireDistinctPlayers || (st.clientsPressed.Count >= cfg.requiredSwitchCount);
        bool withinWindow = unlimited || (now - st.firstPressAt) <= cfg.pressWindowSeconds;

        if (enoughSwitches && distinctOK && withinWindow)
        {
            StartCoroutine(ActivateGroupAfterDelay(groupId, cfg, st));
        }
    }

    private IEnumerator ActivateGroupAfterDelay(int groupId, GroupConfig cfg, GroupState st)
    {
        // ให้คันโยกเล่นพร้อมกัน
        var netIds = new List<ulong>();
        foreach (var sw in st.switchesPressed)
        {
            var nob = sw != null ? sw.GetComponent<NetworkObject>() : null;
            if (nob != null) netIds.Add(nob.NetworkObjectId);
        }
        if (netIds.Count > 0)
            PlayLeversClientRpc(netIds.ToArray());

        if (cfg.extraDelayBeforeActivate > 0f)
            yield return new WaitForSeconds(cfg.extraDelayBeforeActivate);

        if (cfg.extraDelayBeforeActivate > 0f)
            yield return new WaitForSeconds(cfg.extraDelayBeforeActivate);

        bool finalOn = cfg.toggleOnSuccess ? !st.toggleState : true;
        st.toggleState = finalOn;

        ApplyGroupTargets(cfg.targets, finalOn);

        // เล่น Particle Effects
        if (cfg.particles != null && cfg.particles.Count > 0)
        {
            var particleIndices = new List<int>();
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] == cfg) { particleIndices.Add(i); break; }
            }
            if (particleIndices.Count > 0)
                PlayGroupParticlesClientRpc(particleIndices[0], finalOn);
        }

        // ปลดล็อกทุกคน
        foreach (var cid in st.clientsPressed)
            LockClient(cid, false);

        // Clear holders before clearing dictionary
        foreach (var kv in st.pressedBy)
        {
            kv.Key.Server_SetHolder(ulong.MaxValue);
        }

        st.switchesPressed.Clear();
        st.clientsPressed.Clear();
        st.pressedBy.Clear(); // ⭐ เคลียร์ pressedBy ด้วย
        st.firstPressAt = -999f;

        if (cfg.lockAfterSuccess)
            st.locked = true;
    }
    
    private void ApplyGroupTargets(List<TargetEntry> targets, bool groupOn)
    {
        foreach (var t in targets)
        {
            if (!t.allow || t.activatableComponent == null) continue;

            if (t.activatableComponent is IActivatable act)
            {
                bool want = t.invert ? !groupOn : groupOn;
                act.Activate(want);
            }
            else
            {
                Debug.LogWarning($"[CentralSwitchManager] {t.activatableComponent.GetType().Name} ไม่ได้ implement IActivatable");
            }
        }
    }

    [ClientRpc]
    private void PlayLeversClientRpc(ulong[] switchNetIds)
    {
        foreach (var id in switchNetIds)
        {
            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(id, out var nob))
            {
                var sw = nob.GetComponent<InteractSwitch>();
                // โค้ดนี้จะใช้ไม่ได้ถ้า sw.PlayLeverLocal() ไม่ได้มีการเล่น Animation
                // แต่ถ้า PlayLeverLocal มีการส่งสถานะกลับไปหาสวิตช์เพื่อ "คืนค่าคันโยก" ก็จะทำงาน
                // ถ้าต้องการให้คันโยกกลับไปสู่สถานะเดิม (un-pressed) คุณอาจต้องเพิ่ม Logic ใน InteractSwitch.cs
                sw?.PlayLeverLocal(); 
                sw?.PlayActivationSound();
            }
        }
    }

    /// <summary>
    /// เล่น/หยุด ParticleSystem ของ Group บนทุกเครื่อง
    /// </summary>
    [ClientRpc]
    private void PlayGroupParticlesClientRpc(int groupIndex, bool play)
    {
        if (groupIndex < 0 || groupIndex >= groups.Count) return;

        var cfg = groups[groupIndex];
        if (cfg.particles == null) return;

        foreach (var ps in cfg.particles)
        {
            if (ps == null) continue;

            if (play)
            {
                ps.Play(true);
            }
            else
            {
                ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            }
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void ResetGroupServerRpc(int groupId)
    {
        if (!IsServer) return;

        var st = GetOrCreateState(groupId);

        foreach (var cid in st.clientsPressed)
            LockClient(cid, false);

        st.locked = false;
        st.switchesPressed.Clear();
        st.clientsPressed.Clear();
        st.pressedBy.Clear(); // ⭐ เคลียร์ pressedBy ด้วย
        st.firstPressAt = -999f;
        st.toggleState = false;
    }
}