using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

/// <summary>
/// สปาวน์ SpikeTrap แบบสุ่มจุดทีละอัน:
/// - เตือนพื้นของจุดที่จะสปาวน์ให้เป็นสีแดงก่อน (warnBeforeSpawn, warnDuration)
/// - จากนั้นค่อยสปาวน์ SpikeTrap และสั่งให้เล่น 1 รอบ
/// - รอเท่ากับเวลารอบของกับดัก + waitAfterCycle แล้วค่อยสุ่มจุดถัดไป
/// หมายเหตุ: ใช้ร่วมกับ SpikeTrap.cs / ITrapCycle.cs เดิมได้ทันที
/// </summary>
public class SpikeTrapSequentialSpawner : NetworkBehaviour
{
    [Header("Setup")]
    [SerializeField] private SpikeTrap spikeTrapPrefab;
    [SerializeField] private List<SpikeTrapSpawnPoint> spawnPoints = new();

    [Header("Timing")]
    [Tooltip("เวลาที่ต้อง 'รอเพิ่ม' หลังจากกับดักหนึ่งตัวจบรอบ ก่อนจะสุ่มตัวถัดไป")]
    [Min(0f)] public float waitAfterCycle = 0f;

    [Header("Despawn")]
    [Tooltip("เวลาคงอยู่ที่บังคับสำหรับกับดัก (0 = ใช้เวลารอบจริง)")]
    public float lifetimeOverride = 0f;

    [Header("Sequence")]
    [Tooltip("สุ่มให้ครบทุกจุดก่อน (ไม่ซ้ำ) แล้วค่อยรีใหม่")]
    public bool uniqueUntilExhausted = true;

    [Header("Warning")]
    [Tooltip("ให้พื้นของจุดที่กำลังจะสปาวน์ แดงเตือนก่อนหรือไม่")]
    public bool warnBeforeSpawn = true;

    [Min(0f), Tooltip("เวลาที่พื้นแดงเตือนก่อนสปาวน์ (วินาที)")]
    public float warnDuration = 0.8f;

    readonly List<NetworkObject> _alive = new();

    void Reset()
    {
        spawnPoints = new List<SpikeTrapSpawnPoint>(GetComponentsInChildren<SpikeTrapSpawnPoint>(true));
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (!IsServer) return;

        if (spikeTrapPrefab == null)
        {
            Debug.LogError("[SpikeTrapSequentialSpawner] Missing spikeTrapPrefab!");
            return;
        }
        if (spawnPoints == null || spawnPoints.Count == 0)
        {
            Debug.LogError("[SpikeTrapSequentialSpawner] No spawnPoints assigned!");
            return;
        }

        StartCoroutine(SequentialLoop());
    }

    IEnumerator SequentialLoop()
    {
        var bag = new List<int>(spawnPoints.Count);

        while (IsServer)
        {
            if (bag.Count == 0)
            {
                for (int i = 0; i < spawnPoints.Count; i++)
                    bag.Add(i);
            }

            int idx;
            if (uniqueUntilExhausted)
            {
                int pick = UnityEngine.Random.Range(0, bag.Count);
                idx = bag[pick];
                bag.RemoveAt(pick);
            }
            else
            {
                idx = UnityEngine.Random.Range(0, spawnPoints.Count);
            }

            var point = spawnPoints[idx];

            // 1) เตือน "พื้นก้อน" ของจุดนี้ให้เป็นสีแดงก่อน (ซิงค์ทุกเครื่อง)
            if (warnBeforeSpawn && warnDuration > 0f && point != null)
            {
                point.PlayWarningForAll(warnDuration);    // Server -> ทุก client เห็นพร้อมกัน
                yield return new WaitForSeconds(warnDuration);
            }

            // 2) สปาวน์กับดักที่จุดนี้
            var (trap, no) = SpawnAt(point);

            // 2.1) ถ้าจุดนี้ขอ override การตั้งค่า ให้ส่งค่าเข้าไป
            if (point != null && point.overrideSettings)
            {
                trap.ApplyOverrides(point.raiseDistance, point.useLocalSpace);
            }

            // 3) สั่งให้กับดักเล่น 1 รอบ (ขึ้น-ค้าง-ลง) ทันที
            trap.PlayOnceForAll(0f);

            // 4) เวลา wait = เวลารอบจริงของกับดัก + waitAfterCycle
            float cycle = trap.GetCycleDuration();
            float wait = Mathf.Max(0.01f, cycle + waitAfterCycle);

            // 5) ตั้งเวลา despawn (ถ้าอยากบังคับเวลาเอง ให้กำหนด lifetimeOverride > 0)
            float life = lifetimeOverride > 0f ? lifetimeOverride : cycle;
            StartCoroutine(DespawnAfter(no, life));

            yield return new WaitForSeconds(wait);
        }
    }

    (SpikeTrap, NetworkObject) SpawnAt(SpikeTrapSpawnPoint p)
    {
        Vector3 pos = p != null ? p.transform.position : transform.position;
        Quaternion rot = p != null ? p.transform.rotation : transform.rotation;

        var trap = Instantiate(spikeTrapPrefab, pos, rot);
        var no = trap.GetComponent<NetworkObject>();
        if (no == null)
        {
            Debug.LogError("[SpikeTrapSequentialSpawner] SpikeTrap prefab must have NetworkObject.");
        }
        else
        {
            no.Spawn(true); // server owned
            _alive.Add(no);
        }

        return (trap, no);
    }

    IEnumerator DespawnAfter(NetworkObject no, float t)
    {
        yield return new WaitForSeconds(Mathf.Max(0.01f, t));

        if (no != null && no.IsSpawned)
            no.Despawn();

        _alive.Remove(no);
    }
}
