using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;

[RequireComponent(typeof(BoxCollider))]
[DisallowMultipleComponent]
public class BoulderSpawnerOnTrigger : NetworkBehaviour
{
    public enum Axis { X, Y, Z }
    public enum Direction { Positive, Negative }

    [Header("Spawn")]
    [SerializeField] private NetworkObject boulderPrefab;
    [SerializeField] private Transform spawnPoint;
    [SerializeField] private Axis axis = Axis.X;
    [SerializeField] private Direction direction = Direction.Positive;

    [Header("Cadence")]
    [Tooltip("สปอนทุกกี่วินาที (เช่น 2 = ทุก 2 วิ 1 ลูก)")]
    [SerializeField, Min(0.05f)] private float cooldown = 2f;

    [Header("Pooling")]
    [SerializeField] private bool usePooling = true;
    [Tooltip("จำนวนที่อุ่นไว้ในพูลตอนเริ่ม (สำรอง ~5 ลูก)")]
    [SerializeField, Min(0)] private int prewarmCount = 5;

    private readonly Queue<NetworkObject> pool = new();
    private bool isActive = false;
    private Coroutine loopCo;

    void Reset()
    {
        var col = GetComponent<BoxCollider>();
        if (col) col.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // อุ่นพูล (ยังไม่ Spawn)
        if (usePooling && boulderPrefab != null)
        {
            for (int i = 0; i < prewarmCount; i++)
            {
                var no = Instantiate(boulderPrefab);
                no.gameObject.SetActive(false);
                pool.Enqueue(no);
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        if (IsServer) StartCadenceLoop();
        else StartCadenceLoopServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void StartCadenceLoopServerRpc() => StartCadenceLoop();

    private void StartCadenceLoop()
    {
        if (!IsServer || isActive) return;
        isActive = true;
        loopCo = StartCoroutine(LoopSpawnEveryCooldown());
    }

    private IEnumerator LoopSpawnEveryCooldown()
    {
        SpawnOne(); // ยิงทันทีรอบแรก

        var wait = new WaitForSeconds(cooldown);
        while (true)
        {
            yield return wait;
            SpawnOne(); // ยิงต่อไปเรื่อย ๆ ไม่รอหินลูกก่อนหน้าหาย
        }
    }

    private void SpawnOne()
    {
        if (!IsServer || !boulderPrefab || !spawnPoint) return;

        NetworkObject no;
        if (usePooling && pool.Count > 0)
        {
            no = pool.Dequeue();
            no.transform.SetPositionAndRotation(spawnPoint.position, spawnPoint.rotation);

            var rockCfg = no.GetComponent<RollingBoulder>();
            if (rockCfg)
            {
                rockCfg.Configure((RollingBoulder.Axis)axis, (RollingBoulder.Direction)direction);
                rockCfg.returnToPool = usePooling;
            }

            no.gameObject.SetActive(true);
            no.Spawn();
        }
        else
        {
            no = Instantiate(boulderPrefab, spawnPoint.position, spawnPoint.rotation);

            var rockCfg = no.GetComponent<RollingBoulder>();
            if (rockCfg)
            {
                rockCfg.Configure((RollingBoulder.Axis)axis, (RollingBoulder.Direction)direction);
                rockCfg.returnToPool = usePooling;
            }

            no.Spawn();
        }

        var rock = no.GetComponent<RollingBoulder>();
        if (rock != null)
        {
            rock.ServerDespawned -= OnRockDespawned; // กันซ้ำ
            rock.ServerDespawned += OnRockDespawned;
        }
    }

    private void OnRockDespawned(RollingBoulder rock)
    {
        if (!IsServer || rock == null) return;
        rock.ServerDespawned -= OnRockDespawned;

        if (usePooling)
        {
            var no = rock.NetworkObject;
            if (no != null)
            {
                no.gameObject.SetActive(false);
                pool.Enqueue(no);
            }
        }
        // cadence loop ยิงไปเรื่อยอยู่แล้ว — ไม่ต้องทำอะไรเพิ่ม
    }

    private void OnDisable()
    {
        if (loopCo != null) { StopCoroutine(loopCo); loopCo = null; }
        isActive = false;
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!spawnPoint) return;

        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(spawnPoint.position, 0.25f);

        Vector3 dir = axis == Axis.X ? Vector3.right :
                      axis == Axis.Y ? Vector3.up :
                                       Vector3.forward;
        if (direction == Direction.Negative) dir = -dir;

        Gizmos.color = Color.red;
        Gizmos.DrawLine(spawnPoint.position, spawnPoint.position + dir * 2f);
        UnityEditor.Handles.ArrowHandleCap(0, spawnPoint.position, Quaternion.LookRotation(dir), 1.2f, EventType.Repaint);
    }
#endif
}
