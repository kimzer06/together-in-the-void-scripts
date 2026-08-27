using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class WindGroupManager : NetworkBehaviour, ISwitchableWindManager
{
    [Header("Fans (index 0 = ตัวที่ 1)")]
    [SerializeField] private List<MonoBehaviour> fanComponents = new(); // ต้อง implement IActivatable หรือ IWindModeActivatable

    [Header("Indicators (เรียง index ให้ตรงกับ Fans)")]
    [SerializeField] private List<WindStateIndicator> indicators = new();

    [Header("Start State (Server)")]
    [SerializeField] private int startIndex = 0;
    [SerializeField] private bool applyOnServerSpawn = true;

    [Header("Trigger Advance")]
    [SerializeField] private float activationDelay = 0f; // ดีเลย์ตอนกดสวิตช์

    // ====== NEW: Receivers แบบ "รายตำแหน่ง" ให้หมุนตามสถานะจริงของแต่ละ index ======
    [Header("Per-Index Wind Receivers (ขนานกับ fanComponents)")]
    [Tooltip("ใส่คอมโพเนนต์ที่ implement IWindModeActivatable เรียง index ให้ตรงกับ fanComponents เพื่อให้หมุน Push/Pull ตามสถานะจริงของแต่ละตำแหน่ง")]
    [SerializeField] private List<MonoBehaviour> groupReceiversByIndex = new();

    // (ออปชัน) global group receivers ชิ้นเดียว/ไม่ผูก index
    [Header("Global Group Receivers (optional)")]
    [Tooltip("ถ้าอยากมีตัวหมุนรวมทั้งกลุ่ม (1-หลายชิ้น) ให้ตามทิศเดิน dir (+1=Push, -1=Pull) ก็ใส่ไว้ที่นี่ได้ ไม่จำเป็นต้องใช้")]
    [SerializeField] private List<MonoBehaviour> globalGroupReceivers = new();

    // ====== Network State ======
    private NetworkVariable<int> currentIndexNV =
        new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ทิศ “การเดินดัชนี” (ไปข้างหน้า/ถอยหลัง) — ใช้แค่กับ globalGroupReceivers (ถ้าคุณใช้)
    private NetworkVariable<int> directionNV =
        new NetworkVariable<int>(1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // ====== Lifecycle ======
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer && applyOnServerSpawn)
        {
            startIndex = Mathf.Clamp(startIndex, 0, Mathf.Max(0, fanComponents.Count - 1));
            currentIndexNV.Value = startIndex;
            directionNV.Value = 1;

            // ตั้งสถานะครั้งแรก: พัดลม/อินดิเคเตอร์ + ตัวหมุนรายตำแหน่ง
            ApplyStatesServer(currentIndexNV.Value);

            // (ออปชัน) ตัวหมุนรวมทั้งกลุ่มให้ตามทิศเดินปัจจุบัน
            BroadcastGlobalGroupByDir(directionNV.Value);
        }
        else
        {
            // ฝั่ง Client ที่เข้ามาทีหลัง: sync สี + โหมดพัดลมรายตัว + ตัวหมุนรายตำแหน่งให้ครบ
            UpdateIndicatorsClientRpc(currentIndexNV.Value);
            SyncFansAndPerIndexReceiversClientRpc(currentIndexNV.Value);

            // (ออปชัน) sync ตัวหมุนรวมตาม dir ปัจจุบัน
            ApplyGlobalGroupLocal(MapDirToWindMode(directionNV.Value));
        }

        // เผื่อคุณอยากผูก globalGroupReceivers กับการเด้งทิศที่ปลาย
        directionNV.OnValueChanged += (prev, next) =>
        {
            ApplyGlobalGroupLocal(MapDirToWindMode(next));
        };
    }

    // ====== Entry point เรียกจากสวิตช์ ======
    public void Server_OnSwitchPressed(float extraDelay)
    {
        if (!IsServer) return;
        StartCoroutine(AdvanceAfterDelayCo(Mathf.Max(activationDelay, extraDelay)));
    }

    private IEnumerator AdvanceAfterDelayCo(float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        AdvanceOneStepServer();
    }

    // ====== Core Advance ======
    private void AdvanceOneStepServer()
    {
        if (!IsServer || fanComponents.Count == 0) return;

        int idx = currentIndexNV.Value;
        int dir = directionNV.Value;
        int next = idx + dir;

        // เด้งปลายทาง
        if (next >= fanComponents.Count)
        {
            dir = -1;
            next = Mathf.Clamp(fanComponents.Count - 2, 0, Mathf.Max(0, fanComponents.Count - 1));
        }
        else if (next < 0)
        {
            dir = +1;
            next = Mathf.Clamp(1, 0, Mathf.Max(0, fanComponents.Count - 1));
        }

        currentIndexNV.Value = next;
        directionNV.Value = dir;

        // อัปเดตแฟน/อินดิเคเตอร์ + ตัวหมุนรายตำแหน่ง ให้ตรงสถานะจริง
        ApplyStatesServer(next);

        // (ออปชัน) อัปเดตตัวหมุนรวมทั้งกลุ่มตาม dir (ถ้าใช้)
        BroadcastGlobalGroupByDir(dir);
    }

    // ====== Server -> Everyone: ตั้งสถานะรายพัดลม + รายตำแหน่ง ======
    private void ApplyStatesServer(int onIndex)
    {
        // 1) Server ตั้งพัดลมรายตัว (Push เฉพาะ onIndex, อื่นๆ Pull)
        ApplyFansLocal(onIndex);

        // 2) Server ตั้งตัวหมุนรายตำแหน่งให้ตรงกับสถานะจริงของแต่ละ index
        ApplyPerIndexReceiversLocal(onIndex);

        // 3) แจ้ง Indicators ทุกเครื่อง
        UpdateIndicatorsClientRpc(onIndex);

        // 4) สั่งทุก Client ตั้งพัดลม + ตัวหมุนรายตำแหน่ง ให้เหมือนกัน
        SyncFansAndPerIndexReceiversClientRpc(onIndex);
    }

    // ใช้ซ้ำได้ทั้ง Server/Client — พัดลมรายตัว
    private void ApplyFansLocal(int onIndex)
    {
        for (int i = 0; i < fanComponents.Count; i++)
        {
            var mb = fanComponents[i];
            if (!mb) continue;

            if (mb is IWindModeActivatable adv)
            {
                adv.SetWindMode(i == onIndex ? WindMode.Push : WindMode.Pull);
            }
            else if (mb is IActivatable act)
            {
                act.Activate(i == onIndex); // ถ้า false แล้วอยาก map เป็น Pull ให้ทำในคอมโพเนนต์นั้นเอง
            }
            else
            {
                Debug.LogWarning($"[{name}] Fan #{i} ({mb?.GetType().Name}) ไม่ได้ implement IActivatable/IWindModeActivatable");
            }
        }
    }

    // ใช้ซ้ำได้ทั้ง Server/Client — ตัวหมุน "รายตำแหน่ง"
    private void ApplyPerIndexReceiversLocal(int onIndex)
    {
        // ให้ความยาวเท่ากับ fanComponents; ถ้าเกิน/ขาดจะข้าม/ไม่ตั้งตัวนั้น
        int count = Mathf.Min(groupReceiversByIndex.Count, fanComponents.Count);
        for (int i = 0; i < count; i++)
        {
            var r = groupReceiversByIndex[i];
            if (!r) continue;

            if (r is IWindModeActivatable adv)
            {
                adv.SetWindMode(i == onIndex ? WindMode.Push : WindMode.Pull);
            }
            else
            {
                Debug.LogWarning($"[{name}] PerIndexReceiver #{i} ({r?.GetType().Name}) ไม่ได้ implement IWindModeActivatable");
            }
        }
    }

    [ClientRpc]
    private void UpdateIndicatorsClientRpc(int onIndex)
    {
        for (int i = 0; i < indicators.Count; i++)
        {
            var ind = indicators[i];
            if (!ind) continue;
            ind.SetWindMode(i == onIndex ? WindMode.Push : WindMode.Pull);
        }
    }

    // Client ตั้ง “พัดลมรายตัว + ตัวหมุนรายตำแหน่ง” ให้ตรงกับ Server
    [ClientRpc]
    private void SyncFansAndPerIndexReceiversClientRpc(int onIndex)
    {
        ApplyFansLocal(onIndex);
        ApplyPerIndexReceiversLocal(onIndex);
    }

    // ====== (ออปชัน) Global Group Visuals (ไม่ผูก index) ======
    private static WindMode MapDirToWindMode(int dir)
    {
        if (dir > 0) return WindMode.Push;
        if (dir < 0) return WindMode.Pull;
        return WindMode.Disabled;
    }

    private void BroadcastGlobalGroupByDir(int dir)
    {
        BroadcastGlobalGroupByMode(MapDirToWindMode(dir));
    }

    private void BroadcastGlobalGroupByMode(WindMode mode)
    {
        ApplyGlobalGroupLocal(mode);
        ApplyGlobalGroupClientRpc(mode);
    }

    private void ApplyGlobalGroupLocal(WindMode mode)
    {
        for (int i = 0; i < globalGroupReceivers.Count; i++)
        {
            var mb = globalGroupReceivers[i];
            if (!mb) continue;
            if (mb is IWindModeActivatable adv)
                adv.SetWindMode(mode);
        }
    }

    [ClientRpc]
    private void ApplyGlobalGroupClientRpc(WindMode mode)
    {
        ApplyGlobalGroupLocal(mode);
    }

#if UNITY_EDITOR
    [ContextMenu("Advance (Server only)")]
    private void EditorAdvance()
    {
        if (!Application.isPlaying) return;
        if (!IsServer) { Debug.LogWarning("ต้องรันจาก Server"); return; }
        AdvanceOneStepServer();
    }
#endif
}
