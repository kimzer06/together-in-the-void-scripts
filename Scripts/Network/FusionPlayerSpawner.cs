using Fusion;
using UnityEngine;

/// <summary>
/// วางบน scene NetworkObject ในเลเวล — แต่ละเครื่องสปอนตัวละครของตัวเองตอนซีนโหลดเสร็จ
/// (Shared Mode: คนสปอน = state authority ของตัวละครนั้น ไม่ต้องให้ server สปอนให้แบบ NGO)
/// </summary>
public class FusionPlayerSpawner : NetworkBehaviour
{
    [Header("Prefabs for slot 0 / slot 1")]
    [SerializeField] private NetworkObject prefabSlot0;
    [SerializeField] private NetworkObject prefabSlot1;

    [Header("Spawn Points")]
    [SerializeField] private Transform spawn0;
    [SerializeField] private Transform spawn1;

    public override void Spawned()
    {
        var player = Runner.LocalPlayer;
        if (Runner.TryGetPlayerObject(player, out var existing) && existing != null)
            return;

        byte slot = FusionSession.LocalSlot;
        if (slot == FusionSession.NoSlot)
            slot = Runner.IsSharedModeMasterClient ? (byte)0 : (byte)1;

        var prefab  = slot == 0 ? prefabSlot0 : prefabSlot1;
        var spawnPt = slot == 0 ? spawn0 : spawn1;
        if (!prefab || !spawnPt)
        {
            Debug.LogError($"[FusionSpawner] Missing prefab or spawn point for slot {slot}");
            return;
        }

        var obj = Runner.Spawn(prefab, spawnPt.position, spawnPt.rotation, player);
        Runner.SetPlayerObject(player, obj);
        Debug.Log($"[FusionSpawner] Spawned {prefab.name} for {player} (slot {slot}) at {spawnPt.name}");
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        DrawSpawnGizmo(spawn0, Color.green, "Slot 0");
        DrawSpawnGizmo(spawn1, Color.cyan, "Slot 1");
    }

    private static void DrawSpawnGizmo(Transform point, Color color, string label)
    {
        if (!point) return;
        Gizmos.color = color;
        Gizmos.DrawWireSphere(point.position, 0.3f);
        Vector3 origin = point.position + Vector3.up * 0.5f;
        Gizmos.DrawLine(origin, origin + point.forward * 1.5f);
        UnityEditor.Handles.color = color;
        UnityEditor.Handles.Label(origin + Vector3.up * 0.3f, label);
    }
#endif
}
